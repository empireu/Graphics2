using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World.Storage;
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator

namespace WinterGame.World;

public enum SectionStatus
{
    /// <summary>
    ///     The section is not loaded.
    /// </summary>
    None,
    /// <summary>
    ///     The section is being generated off-thread.
    /// </summary>
    Generating,
    /// <summary>
    ///     The section is loaded fully.
    /// </summary>
    Ready
}

/// <summary>
///     The world, held loaded by <see cref="LevelTicket"/>s.
///     Needed sections are generated off-thread nearest first, one per free executor slot per tick, and a section entirely above the surface is written as uniform air without a job.
///     Every public member is game thread only.
/// </summary>
public sealed class Level : IDisposable
{
    /// <summary>
    ///     Sections past a ticket's shape that stay loaded once loaded, so a loader moving back and forth across a boundary does not reload it.
    /// </summary>
    public const int KeepMargin = 1;

    /// <summary>
    ///     Released sections one <see cref="Update"/> destroys by default.
    /// </summary>
    public const int DefaultUnloadsPerUpdate = 512;

    /// <summary>
    ///     What a row walk does to every section of a row.
    /// </summary>
    private enum RowOperation
    {
        None,
        /// <summary>
        ///     Adds a ticket reference, creating the entry if there is none.
        /// </summary>
        Claim,
        /// <summary>
        ///     Removes a ticket reference, which unloads the section once none is left.
        /// </summary>
        Release,
        /// <summary>
        ///     Makes a section that is neither generating nor loaded pending. Only for load shapes, whose sections always have an entry.
        /// </summary>
        Want
    }

    private struct SectionEntry
    {
        public SectionPos Position;

        /// <summary>
        ///     Tickets whose keep shape covers the section.
        /// </summary>
        public int TicketCount;

        public SectionStatus Status;

        /// <summary>
        ///     True while the key is in the unload queue, which it enters when the ticket count of a loaded section reaches zero.
        /// </summary>
        public bool UnloadQueued;

        /// <summary>
        ///     The job generating the section while <see cref="Status"/> is <see cref="SectionStatus.Generating"/>.
        /// </summary>
        public Job Job;

        /// <summary>
        ///     The section in the level's arena while <see cref="Status"/> is <see cref="SectionStatus.Ready"/>.
        /// </summary>
        public Section Section;
    }

    private struct InFlightGeneration
    {
        public ulong Key;
        public Job Job;
        public int Slot;

        /// <summary>
        ///     False while the scheduler has refused the publish. A job that never ran cannot be released, so it is kept and published again on the next tick.
        /// </summary>
        public bool Published;
    }

    private readonly JobScheduler _scheduler;
    private readonly SectionGenerationExecutor _executor;
    private readonly int _providerId;

    /// <summary>
    ///     The lowest world height in meters at which a section is known to be air, one sample above the highest surface, since content only reaches zero one sample past the surface.
    /// </summary>
    private readonly float _skyMinimumY;

    private readonly int _unloadsPerUpdate;

    private readonly SectionArena _terrain = new();
    private readonly Dictionary<ulong, SectionEntry> _sections = [];

    /// <summary>
    ///     Sections inside some ticket's load shape that are neither generating nor loaded.
    ///     A section that has left every load shape since is only dropped by the next <see cref="RebuildLoadQueue"/>, so a move does no work for the sections leaving a load shape.
    /// </summary>
    private readonly HashSet<ulong> _pending = [];

    /// <summary>
    ///     Pending sections the last rebuild found outside every load shape, removed once the rebuild has walked the set.
    /// </summary>
    private readonly List<ulong> _stalePending = [];

    /// <summary>
    ///     Loaded sections whose ticket count reached zero, oldest first. Each <see cref="Update"/> destroys up to <see cref="_unloadsPerUpdate"/> of them, and skips one a ticket has claimed again since.
    /// </summary>
    private readonly Queue<ulong> _unloadQueue = new();

    /// <summary>
    ///     The pending sections inside some ticket's shape, by squared distance to the nearest ticket center.
    ///     Rebuilt from <see cref="_pending"/> after a ticket changes, and entries that went stale since are skipped when dequeued.
    /// </summary>
    private readonly PriorityQueue<ulong, long> _loadQueue = new();

    private readonly List<InFlightGeneration> _inFlight = [];

    /// <summary>
    ///     Sections loaded by <see cref="GetOrLoadSection"/> without a ticket, unloaded on the next <see cref="Update"/> unless a ticket has claimed them since.
    /// </summary>
    private readonly List<ulong> _unticketedLoads = [];

    private readonly List<LevelTicket> _tickets = [];

    private bool _loadQueueDirty;
    private bool _disposed;

    /// <summary>
    ///     Creates a level and registers its generation executor.
    /// </summary>
    public Level(
        JobScheduler scheduler,
        TerrainGraph graph,
        int slotCount = SectionGenerationExecutor.DefaultSlotCount,
        int unloadsPerUpdate = DefaultUnloadsPerUpdate)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(unloadsPerUpdate, 1);

        if (scheduler.IsStarted)
        {
            throw new InvalidOperationException("The level registers its generation executor, so it has to be created before the scheduler starts.");
        }

        _scheduler = scheduler;
        _executor = new SectionGenerationExecutor(graph, scheduler.ThreadCount, slotCount);
        _providerId = scheduler.RegisterExecutor(_executor);
        _skyMinimumY = graph.MaxSurfaceY + WorldConstants.VoxelSize;
        _unloadsPerUpdate = unloadsPerUpdate;
    }

    internal int PendingCount => _pending.Count;

    internal int InFlightCount => _inFlight.Count;

    /// <summary>
    ///     Released sections waiting to be destroyed, the backlog a long move leaves behind.
    /// </summary>
    public int UnloadQueueCount => _unloadQueue.Count;

    internal int SectionEntryCount => _sections.Count;

    internal int GetTicketCount(SectionPos position)
    {
        return _sections.TryGetValue(position.GetKey(), out var entry) ? entry.TicketCount : 0;
    }

    internal bool IsPending(SectionPos position)
    {
        return _pending.Contains(position.GetKey());
    }

    /// <summary>
    ///     Adds a ticket and claims every section in its keep shape. The sections load over the following ticks.
    /// </summary>
    public LevelTicket AddTicket(SectionPos center, int horizontalRadius, int verticalRadius)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(horizontalRadius);
        ArgumentOutOfRangeException.ThrowIfNegative(verticalRadius);

        var ticket = new LevelTicket(this, center, horizontalRadius, verticalRadius);

        _tickets.Add(ticket);
        ApplyShape(ticket.KeepShape, center, RowOperation.Claim);
        ApplyShape(ticket.LoadShape, center, RowOperation.Want);
        _loadQueueDirty = true;

        return ticket;
    }

    /// <summary>
    ///     Moves a ticket, claiming the sections that entered its keep shape and releasing the ones that left it.
    ///     The work is the rows of the box around both placements plus the sections that changed.
    /// </summary>
    public void MoveTicket(LevelTicket ticket, SectionPos center)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateTicket(ticket);

        var previous = ticket.Center;

        if (previous == center)
        {
            return;
        }

        ticket.Center = center;

        var keepShape = ticket.KeepShape;

        // Placements that cannot overlap are applied whole, since the difference would walk every row between them.
        // The new shape is claimed before the old one is released:
        if (!keepShape.CanOverlap(previous, center))
        {
            ApplyShape(keepShape, center, RowOperation.Claim);
            ApplyShape(ticket.LoadShape, center, RowOperation.Want);
            ApplyShape(keepShape, previous, RowOperation.Release);
            _loadQueueDirty = true;

            return;
        }

        // The keep difference goes first, since a section entering the load shape needs the entry that claiming it creates:
        ApplyShapeDifference(keepShape, previous, center, RowOperation.Claim, RowOperation.Release);
        ApplyShapeDifference(ticket.LoadShape, previous, center, RowOperation.Want, RowOperation.None);
        _loadQueueDirty = true;
    }

    /// <summary>
    ///     Removes a ticket, releasing every section of its keep shape. The sections no other ticket keeps are unloaded by the following ticks.
    /// </summary>
    public void RemoveTicket(LevelTicket ticket)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateTicket(ticket);

        ApplyShape(ticket.KeepShape, ticket.Center, RowOperation.Release);
        _tickets.Remove(ticket);
        ticket.Owner = null;
        _loadQueueDirty = true;
    }

    /// <summary>
    ///     Unloads the previous tick's unticketed loads and up to the budget of released sections, copies finished generations into the level, and publishes the nearest wanted sections into the free executor slots.
    /// </summary>
    public void Update()
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        _scheduler.Pool.ThrowIfFaulted();

        UnloadUnticketedLoads();
        UnloadReleasedSections();
        HarvestGenerations();

        if (!RepublishRefusedGenerations())
        {
            return;
        }

        if (_loadQueueDirty)
        {
            RebuildLoadQueue();
        }

        PublishGenerations();
    }

    public LevelStats GetStats()
    {
        WgAssert.DebugIsOnGameThread();

        return new LevelStats(_terrain.SectionCount, _inFlight.Count, _pending.Count, _unloadQueue.Count);
    }

    public SectionStatus GetStatus(SectionPos position)
    {
        WgAssert.DebugIsOnGameThread();

        return _sections.TryGetValue(position.GetKey(), out var entry) ? entry.Status : SectionStatus.None;
    }

    public bool TryGetSection(SectionPos position, out Section section)
    {
        WgAssert.DebugIsOnGameThread();

        if (_sections.TryGetValue(position.GetKey(), out var entry) && entry.Status == SectionStatus.Ready)
        {
            section = entry.Section;
            return true;
        }

        section = default;
        return false;
    }

    /// <summary>
    ///     Returns the section, loading it on the calling thread if it is not loaded.
    ///     A generation already in flight for it is discarded when it completes.
    ///     A section no ticket keeps can be unloaded by the next <see cref="Update"/>, so a caller that needs it for longer needs a ticket.
    /// </summary>
    public Section GetOrLoadSection(SectionPos position)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = position.GetKey();

        if (_sections.TryGetValue(key, out var existing) && existing.Status == SectionStatus.Ready)
        {
            return existing.Section;
        }

        var section = IsSky(position) ? WriteSky(position) : _executor.GenerateNow(position, _terrain);

        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_sections, key, out var exists);

        if (!exists)
        {
            entry.Position = position;
        }

        _pending.Remove(key);

        entry.Status = SectionStatus.Ready;
        entry.Job = default;
        entry.Section = section;

        if (entry.TicketCount == 0)
        {
            _unticketedLoads.Add(key);
        }

        return section;
    }

    /// <summary>
    ///     Reads both channels of the inclusive box out of the loaded sections, expanding uniform bricks and copying dense ones.
    ///     A section that is not loaded reads as air, which is content 0 and the null material, and the result is false when the box touched one.
    ///     Both destinations are sized <c>maxInclusive - min + 1</c> on every axis.
    /// </summary>
    public bool ReadRange(Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material)
    {
        WgAssert.DebugIsOnGameThread();
        ValidateRange(min, maxInclusive, content, material);

        var complete = true;
        var sectionMin = min >> SectionPos.Log;
        var sectionMax = maxInclusive >> SectionPos.Log;

        for (var sectionZ = sectionMin.Z; sectionZ <= sectionMax.Z; sectionZ++)
        {
            for (var sectionY = sectionMin.Y; sectionY <= sectionMax.Y; sectionY++)
            {
                for (var sectionX = sectionMin.X; sectionX <= sectionMax.X; sectionX++)
                {
                    var position = new SectionPos(sectionX, sectionY, sectionZ);
                    var origin = position.GetOrigin();
                    var clipMin = Vector3i.Max(min, origin);
                    var clipMax = Vector3i.Min(maxInclusive, origin + SectionPos.SectionMask);

                    if (TryGetSection(position, out var section))
                    {
                        ReadSection(section, clipMin - origin, clipMax - origin, origin - min, content, material);
                        continue;
                    }

                    complete = false;
                    FillBox(content, clipMin - min, clipMax - min, VoxelChannel.Content.GetClearValue());
                    FillBox(material, clipMin - min, clipMax - min, VoxelChannel.Material.GetClearValue());
                }
            }
        }

        return complete;
    }

    /// <summary>
    ///     Reads one voxel out of its loaded section. A section that is not loaded reads as air and the result is false.
    /// </summary>
    public bool ReadPoint(Vector3i position, out byte content, out byte material)
    {
        WgAssert.DebugIsOnGameThread();

        var sectionPosition = new SectionPos(
            SectionPos.VoxelToSectionCoord(position.X),
            SectionPos.VoxelToSectionCoord(position.Y),
            SectionPos.VoxelToSectionCoord(position.Z)
        );

        if (!TryGetSection(sectionPosition, out var section))
        {
            content = VoxelChannel.Content.GetClearValue();
            material = VoxelChannel.Material.GetClearValue();

            return false;
        }

        var local = position & SectionPos.SectionMask;
        var brick = local >> Section.BrickLog;
        var inBrick = local & (Section.BrickSize - 1);
        var brickIndex = Section.GetBrickIndex(brick.X, brick.Y, brick.Z);
        var voxelIndex = Section.GetBrickVoxelIndex(inBrick.X, inBrick.Y, inBrick.Z);

        content = ReadVoxel(section, VoxelChannel.Content, brickIndex, voxelIndex);
        material = ReadVoxel(section, VoxelChannel.Material, brickIndex, voxelIndex);

        return true;
    }

    /// <summary>
    ///     Waits for every generation in flight and returns its slot and job.
    ///     The scheduler has to still be running, or drained when it was never started, since the waits would otherwise not return.
    /// </summary>
    public void Dispose()
    {
        WgAssert.DebugIsOnGameThread();

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var inFlight in _inFlight)
        {
            // A refused job never ran, so it can neither complete nor be released:
            if (!inFlight.Published)
            {
                continue;
            }

            _scheduler.Pool.Complete(inFlight.Job);
            _executor.Free(inFlight.Slot);
            _scheduler.Pool.Release(inFlight.Job);
        }

        _inFlight.Clear();
    }

    private void ValidateTicket(LevelTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (ticket.Owner != this)
        {
            throw new ArgumentException("The ticket is not active in this level.", nameof(ticket));
        }
    }

    private static void ValidateRange(Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material)
    {
        if (maxInclusive.X < min.X || maxInclusive.Y < min.Y || maxInclusive.Z < min.Z)
        {
            throw new ArgumentException(
                $"The box max ({maxInclusive.X}, {maxInclusive.Y}, {maxInclusive.Z}) is below its min ({min.X}, {min.Y}, {min.Z}) on some axis.",
                nameof(maxInclusive)
            );
        }

        var size = maxInclusive - min + 1;

        ValidateDestination(content, size, nameof(content));
        ValidateDestination(material, size, nameof(material));
    }

    private static void ValidateDestination(Span3D<byte> destination, Vector3i size, string name)
    {
        if (destination.SizeX != size.X || destination.SizeY != size.Y || destination.SizeZ != size.Z)
        {
            throw new ArgumentException(
                $"The destination is {destination.SizeX} x {destination.SizeY} x {destination.SizeZ}, but the box is {size.X} x {size.Y} x {size.Z}.",
                name
            );
        }
    }

    /// <summary>
    ///     Copies a box of one section, given in its local voxel coordinates, into destinations that hold the section's local origin at <paramref name="destinationOffset"/>.
    /// </summary>
    private static void ReadSection(Section section, Vector3i localMin, Vector3i localMax, Vector3i destinationOffset, Span3D<byte> content, Span3D<byte> material)
    {
        var brickMin = localMin >> Section.BrickLog;
        var brickMax = localMax >> Section.BrickLog;

        for (var brickZ = brickMin.Z; brickZ <= brickMax.Z; brickZ++)
        {
            for (var brickY = brickMin.Y; brickY <= brickMax.Y; brickY++)
            {
                for (var brickX = brickMin.X; brickX <= brickMax.X; brickX++)
                {
                    var brickOrigin = new Vector3i(brickX, brickY, brickZ) << Section.BrickLog;
                    var clipMin = Vector3i.Max(localMin, brickOrigin);
                    var clipMax = Vector3i.Min(localMax, brickOrigin + (Section.BrickSize - 1));
                    var brickIndex = Section.GetBrickIndex(brickX, brickY, brickZ);

                    ReadBrick(section, VoxelChannel.Content, brickIndex, brickOrigin, clipMin, clipMax, destinationOffset, content);
                    ReadBrick(section, VoxelChannel.Material, brickIndex, brickOrigin, clipMin, clipMax, destinationOffset, material);
                }
            }
        }
    }

    /// <summary>
    ///     Copies the clipped part of one brick one row at a time.
    ///     A uniform brick fills each row with its value and a dense one copies each row out of its array.
    /// </summary>
    private static void ReadBrick(
        Section section,
        VoxelChannel channel,
        int brickIndex,
        Vector3i brickOrigin,
        Vector3i clipMin,
        Vector3i clipMax,
        Vector3i destinationOffset,
        Span3D<byte> destination)
    {
        var rowLength = clipMax.X - clipMin.X + 1;
        var data = destination.Data;

        if (section.GetBrickType(channel, brickIndex) == BrickType.Uniform)
        {
            var value = section.GetUniformValue(channel, brickIndex);

            for (var z = clipMin.Z; z <= clipMax.Z; z++)
            {
                for (var y = clipMin.Y; y <= clipMax.Y; y++)
                {
                    var row = FlatIndex(destination, clipMin.X + destinationOffset.X, y + destinationOffset.Y, z + destinationOffset.Z);

                    data.Slice(row, rowLength).Fill(value);
                }
            }

            return;
        }

        var voxels = section.GetDense(channel, brickIndex).Span;

        for (var z = clipMin.Z; z <= clipMax.Z; z++)
        {
            for (var y = clipMin.Y; y <= clipMax.Y; y++)
            {
                var source = Section.GetBrickVoxelIndex(clipMin.X - brickOrigin.X, y - brickOrigin.Y, z - brickOrigin.Z);
                var row = FlatIndex(destination, clipMin.X + destinationOffset.X, y + destinationOffset.Y, z + destinationOffset.Z);

                voxels.Slice(source, rowLength).CopyTo(data.Slice(row, rowLength));
            }
        }
    }

    private static byte ReadVoxel(Section section, VoxelChannel channel, int brickIndex, int voxelIndex)
    {
        return section.GetBrickType(channel, brickIndex) == BrickType.Uniform
            ? section.GetUniformValue(channel, brickIndex)
            : section.GetDense(channel, brickIndex).Span[voxelIndex];
    }

    private static void FillBox(Span3D<byte> destination, Vector3i min, Vector3i maxInclusive, byte value)
    {
        var rowLength = maxInclusive.X - min.X + 1;
        var data = destination.Data;

        for (var z = min.Z; z <= maxInclusive.Z; z++)
        {
            for (var y = min.Y; y <= maxInclusive.Y; y++)
            {
                data.Slice(FlatIndex(destination, min.X, y, z), rowLength).Fill(value);
            }
        }
    }

    // TODO unify all these
    private static int FlatIndex(Span3D<byte> span, int x, int y, int z)
    {
        return x + (y + z * span.SizeY) * span.SizeX;
    }

    /// <summary>
    ///     Applies the operation to every section of the shape placed at <paramref name="center"/>.
    /// </summary>
    private void ApplyShape(TicketShape shape, SectionPos center, RowOperation operation)
    {
        for (var z = center.Z - shape.HorizontalRadius; z <= center.Z + shape.HorizontalRadius; z++)
        {
            for (var y = center.Y - shape.VerticalRadius; y <= center.Y + shape.VerticalRadius; y++)
            {
                if (shape.TryGetRow(center, y, z, out var minX, out var maxX))
                {
                    ApplyRow(operation, minX, maxX, y, z);
                }
            }
        }
    }

    /// <summary>
    ///     Applies <paramref name="entering"/> to the sections inside the shape at <paramref name="center"/> and outside it at <paramref name="previous"/>, and <paramref name="leaving"/> to the reverse.
    ///     Each row of the two placements is one X interval, so a row's difference is at most two intervals per direction and no section is tested on its own.
    /// </summary>
    private void ApplyShapeDifference(TicketShape shape, SectionPos previous, SectionPos center, RowOperation entering, RowOperation leaving)
    {
        var minY = Math.Min(previous.Y, center.Y) - shape.VerticalRadius;
        var maxY = Math.Max(previous.Y, center.Y) + shape.VerticalRadius;
        var minZ = Math.Min(previous.Z, center.Z) - shape.HorizontalRadius;
        var maxZ = Math.Max(previous.Z, center.Z) + shape.HorizontalRadius;

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                var wasInside = shape.TryGetRow(previous, y, z, out var previousMinX, out var previousMaxX);
                var isInside = shape.TryGetRow(center, y, z, out var minX, out var maxX);

                if (isInside)
                {
                    ApplyRowOutside(entering, minX, maxX, wasInside, previousMinX, previousMaxX, y, z);
                }

                if (wasInside && leaving != RowOperation.None)
                {
                    ApplyRowOutside(leaving, previousMinX, previousMaxX, isInside, minX, maxX, y, z);
                }
            }
        }
    }

    /// <summary>
    ///     Applies the operation to the part of the row from <paramref name="minX"/> to <paramref name="maxX"/> outside the excluded interval, when there is one.
    /// </summary>
    private void ApplyRowOutside(RowOperation operation, int minX, int maxX, bool hasExcluded, int excludedMinX, int excludedMaxX, int y, int z)
    {
        if (!hasExcluded || excludedMaxX < minX || excludedMinX > maxX)
        {
            ApplyRow(operation, minX, maxX, y, z);
            return;
        }

        if (minX < excludedMinX)
        {
            ApplyRow(operation, minX, excludedMinX - 1, y, z);
        }

        if (maxX > excludedMaxX)
        {
            ApplyRow(operation, excludedMaxX + 1, maxX, y, z);
        }
    }

    private void ApplyRow(RowOperation operation, int minX, int maxX, int y, int z)
    {
        switch (operation)
        {
            case RowOperation.None:
                break;
            case RowOperation.Claim:
                for (var x = minX; x <= maxX; x++)
                {
                    AddTicketReference(new SectionPos(x, y, z));
                }

                break;
            case RowOperation.Release:
                for (var x = minX; x <= maxX; x++)
                {
                    RemoveTicketReference(new SectionPos(x, y, z));
                }

                break;
            case RowOperation.Want:
                for (var x = minX; x <= maxX; x++)
                {
                    MarkWanted(new SectionPos(x, y, z));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private void AddTicketReference(SectionPos position)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_sections, position.GetKey(), out var exists);

        if (!exists)
        {
            entry.Position = position;
        }

        entry.TicketCount++;
    }

    private void RemoveTicketReference(SectionPos position)
    {
        var key = position.GetKey();
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, key);

        Debug.Assert(!Unsafe.IsNullRef(ref entry), $"Released a ticket reference on section ({position.X}, {position.Y}, {position.Z}), which no ticket claimed.");

        entry.TicketCount--;

        if (entry.TicketCount > 0)
        {
            return;
        }

        switch (entry.Status)
        {
            case SectionStatus.None:
                _pending.Remove(key);
                _sections.Remove(key);
                break;
            case SectionStatus.Ready:
                // A queued section claimed again before its turn is skipped then, so it is queued at most once:
                if (!entry.UnloadQueued)
                {
                    entry.UnloadQueued = true;
                    _unloadQueue.Enqueue(key);
                }

                break;
            case SectionStatus.Generating:
                // The entry stays until the generation completes, and the harvest discards it then.
                break;
            default:
                throw new InvalidOperationException($"Section status {entry.Status} is not handled.");
        }
    }

    private void MarkWanted(SectionPos position)
    {
        var key = position.GetKey();
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, key);

        Debug.Assert(!Unsafe.IsNullRef(ref entry), $"Section ({position.X}, {position.Y}, {position.Z}) entered a load shape without being claimed by the keep shape around it.");

        if (entry.Status == SectionStatus.None)
        {
            _pending.Add(key);
        }
    }

    private void UnloadUnticketedLoads()
    {
        foreach (var key in _unticketedLoads)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, key);

            // A section a ticket claimed and released since is in the unload queue, which owns its unload now:
            if (Unsafe.IsNullRef(ref entry) || entry.TicketCount > 0 || entry.Status != SectionStatus.Ready || entry.UnloadQueued)
            {
                continue;
            }

            _terrain.Destroy(entry.Position);
            _sections.Remove(key);
        }

        _unticketedLoads.Clear();
    }

    /// <summary>
    ///     Destroys up to <see cref="_unloadsPerUpdate"/> queued sections, oldest first. A section a ticket claimed again since it was queued is dropped from the queue and kept.
    /// </summary>
    private void UnloadReleasedSections()
    {
        var unloaded = 0;

        while (unloaded < _unloadsPerUpdate && _unloadQueue.TryDequeue(out var key))
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, key);

            Debug.Assert(
                !Unsafe.IsNullRef(ref entry) && entry.UnloadQueued && entry.Status == SectionStatus.Ready,
                "Only the unload queue removes a queued entry, and a queued entry stays loaded until then."
            );

            entry.UnloadQueued = false;

            if (entry.TicketCount > 0)
            {
                continue;
            }

            _terrain.Destroy(entry.Position);
            _sections.Remove(key);
            unloaded++;
        }
    }

    private void HarvestGenerations()
    {
        var pool = _scheduler.Pool;

        for (var index = _inFlight.Count - 1; index >= 0; index--)
        {
            var inFlight = _inFlight[index];

            if (!inFlight.Published || !pool.TryComplete(inFlight.Job))
            {
                continue;
            }

            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, inFlight.Key);

            // An entry that no longer names this job was unloaded or loaded synchronously since, so the result is dropped:
            if (!Unsafe.IsNullRef(ref entry) && entry.Status == SectionStatus.Generating && entry.Job == inFlight.Job)
            {
                if (entry.TicketCount > 0)
                {
                    entry.Section = _terrain.CreateCopy(_executor.GetResult(inFlight.Slot));
                    entry.Status = SectionStatus.Ready;
                    entry.Job = default;
                }
                else
                {
                    _sections.Remove(inFlight.Key);
                }
            }

            _executor.Free(inFlight.Slot);
            pool.Release(inFlight.Job);

            _inFlight[index] = _inFlight[^1];
            _inFlight.RemoveAt(_inFlight.Count - 1);
        }
    }

    /// <summary>
    ///     Publishes the jobs the scheduler refused on an earlier tick. False means it refused again, and nothing new should be published this tick.
    /// </summary>
    private bool RepublishRefusedGenerations()
    {
        var span = CollectionsMarshal.AsSpan(_inFlight);

        for (var index = 0; index < span.Length; index++)
        {
            ref var inFlight = ref span[index];

            if (inFlight.Published)
            {
                continue;
            }

            if (!_scheduler.Publish(inFlight.Job, _providerId, inFlight.Slot))
            {
                return false;
            }

            inFlight.Published = true;
        }

        return true;
    }

    private void RebuildLoadQueue()
    {
        _loadQueue.Clear();

        foreach (var key in _pending)
        {
            if (TryGetLoadPriority(new SectionPos(key), out var priority))
            {
                _loadQueue.Enqueue(key, priority);
            }
            else
            {
                _stalePending.Add(key);
            }
        }

        foreach (var key in _stalePending)
        {
            _pending.Remove(key);
        }

        _stalePending.Clear();
        _loadQueueDirty = false;
    }

    /// <summary>
    ///     The squared distance from the section to the nearest ticket whose load shape holds it. False when no load shape holds it.
    /// </summary>
    private bool TryGetLoadPriority(SectionPos position, out long priority)
    {
        priority = long.MaxValue;

        foreach (var ticket in _tickets)
        {
            var center = ticket.Center;

            if (!ticket.LoadShape.Contains(center, position.X, position.Y, position.Z))
            {
                continue;
            }

            long deltaX = position.X - center.X;
            long deltaY = position.Y - center.Y;
            long deltaZ = position.Z - center.Z;

            priority = Math.Min(priority, deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
        }

        return priority != long.MaxValue;
    }

    private void PublishGenerations()
    {
        var pool = _scheduler.Pool;

        while (_executor.FreeSlotCount > 0 && _loadQueue.TryDequeue(out var key, out _))
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, key);

            if (Unsafe.IsNullRef(ref entry) || entry.Status != SectionStatus.None)
            {
                continue;
            }

            _pending.Remove(key);

            if (IsSky(entry.Position))
            {
                entry.Section = WriteSky(entry.Position);
                entry.Status = SectionStatus.Ready;
                continue;
            }

            var slot = _executor.Allocate(entry.Position);
            var job = pool.CreateJob();
            var published = _scheduler.Publish(job, _providerId, slot);

            entry.Status = SectionStatus.Generating;
            entry.Job = job;

            _inFlight.Add(
                new InFlightGeneration
                {
                    Key = key,
                    Job = job,
                    Slot = slot,
                    Published = published
                }
            );

            if (!published)
            {
                return;
            }
        }
    }

    private bool IsSky(SectionPos position)
    {
        return SectionPos.SectionToVoxelCoord(position.Y) * WorldConstants.VoxelSize >= _skyMinimumY;
    }

    private Section WriteSky(SectionPos position)
    {
        return _terrain.CreateUniform(position, 0, SurfaceMaterial.Null);
    }
}

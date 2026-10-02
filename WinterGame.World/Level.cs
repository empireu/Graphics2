using System.Diagnostics;
using System.Numerics;
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

    /// <summary>
    ///     One X interval of one row and what to do to its sections.
    /// </summary>
    private readonly struct RowRun(int minX, int maxX, int y, int z, RowOperation operation)
    {
        public readonly int MinX = minX;
        public readonly int MaxX = maxX;
        public readonly int Y = y;
        public readonly int Z = z;
        public readonly RowOperation Operation = operation;
    }

    /// <summary>
    ///     The most runs one row of blocks can hold: each of its rows has at most two intervals entering a shape and two leaving it.
    /// </summary>
    private const int MaxRunsPerBlockRow = SectionBlockPos.BlockSize * SectionBlockPos.BlockSize * 4;

    /// <summary>
    ///     The widest row of blocks whose scratch is taken from the stack.
    /// </summary>
    private const int MaxStackBlocksPerRow = 256;

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

    /// <summary>
    ///     Every block holding a section that has an entry, by block key. A section has an entry while it is claimed, generating or ready, and a block left with none is freed.
    /// </summary>
    private readonly Dictionary<ulong, SectionBlock> _blocks = [];

    private readonly SectionBlockAllocator _blockStorage = new();

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
    ///     Counts the blocks added and removed, so a <see cref="FastSectionAccessor"/> can check that the block it remembers is still the level's.
    /// </summary>
    private int _blockVersion;

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

    internal int SectionEntryCount
    {
        get
        {
            var count = 0;

            foreach (var block in _blocks.Values)
            {
                count += BitOperations.PopCount(_blockStorage.Get(block).Occupied);
            }

            return count;
        }
    }

    internal int BlockVersion => _blockVersion;

    internal int GetTicketCount(SectionPos position)
    {
        return TryGetBlock(position, out _, out var view) ? view.TicketCounts[SectionBlockPos.GetLocalIndex(position)] : 0;
    }

    internal bool IsPending(SectionPos position)
    {
        return _pending.Contains(position.GetKey());
    }

    internal bool IsUnloadQueued(SectionPos position)
    {
        return TryGetBlock(position, out _, out var view) && (view.UnloadQueued & GetBit(position)) != 0;
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

        return _blocks.TryGetValue(SectionBlockPos.FromSection(position).GetKey(), out var block) ? GetStatus(block, GetBit(position)) : SectionStatus.None;
    }

    public bool TryGetSection(SectionPos position, out Section section)
    {
        WgAssert.DebugIsOnGameThread();

        if (_blocks.TryGetValue(SectionBlockPos.FromSection(position).GetKey(), out var block))
        {
            return TryGetSection(block, position, out section);
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

        if (TryGetSection(position, out var loaded))
        {
            return loaded;
        }

        var key = position.GetKey();
        var section = IsSky(position) ? WriteSky(position) : _executor.GenerateNow(position, _terrain);
        var view = GetOrAddBlock(SectionBlockPos.FromSection(position).GetKey());
        var index = SectionBlockPos.GetLocalIndex(position);
        var bit = 1UL << index;

        _pending.Remove(key);

        view.Slots[index] = section.SlotIndex;
        view.Generating &= ~bit;
        view.Ready |= bit;

        if (view.TicketCounts[index] == 0)
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

        var accessor = new FastSectionAccessor(this);
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

                    if (accessor.TryGetSection(position, out var section))
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
        return new FastSectionAccessor(this).ReadPoint(position, out content, out material);
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

    internal static byte ReadVoxel(Section section, VoxelChannel channel, int brickIndex, int voxelIndex)
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
        Span<RowRun> runs = stackalloc RowRun[MaxRunsPerBlockRow];

        var minY = center.Y - shape.VerticalRadius;
        var maxY = center.Y + shape.VerticalRadius;
        var minZ = center.Z - shape.HorizontalRadius;
        var maxZ = center.Z + shape.HorizontalRadius;

        for (var blockZ = minZ >> SectionBlockPos.Log; blockZ <= maxZ >> SectionBlockPos.Log; blockZ++)
        {
            for (var blockY = minY >> SectionBlockPos.Log; blockY <= maxY >> SectionBlockPos.Log; blockY++)
            {
                var count = 0;

                for (var z = Math.Max(minZ, blockZ << SectionBlockPos.Log); z <= Math.Min(maxZ, (blockZ << SectionBlockPos.Log) + SectionBlockPos.BlockMask); z++)
                {
                    for (var y = Math.Max(minY, blockY << SectionBlockPos.Log); y <= Math.Min(maxY, (blockY << SectionBlockPos.Log) + SectionBlockPos.BlockMask); y++)
                    {
                        if (shape.TryGetRow(center, y, z, out var minX, out var maxX))
                        {
                            runs[count++] = new RowRun(minX, maxX, y, z, operation);
                        }
                    }
                }

                ApplyRuns(runs[..count], blockY, blockZ);
            }
        }
    }

    /// <summary>
    ///     Applies <paramref name="entering"/> to the sections inside the shape at <paramref name="center"/> and outside it at <paramref name="previous"/>, and <paramref name="leaving"/> to the reverse.
    ///     Each row of the two placements is one X interval, so a row's difference is at most two intervals per direction and no section is tested on its own.
    /// </summary>
    private void ApplyShapeDifference(TicketShape shape, SectionPos previous, SectionPos center, RowOperation entering, RowOperation leaving)
    {
        Span<RowRun> runs = stackalloc RowRun[MaxRunsPerBlockRow];

        var minY = Math.Min(previous.Y, center.Y) - shape.VerticalRadius;
        var maxY = Math.Max(previous.Y, center.Y) + shape.VerticalRadius;
        var minZ = Math.Min(previous.Z, center.Z) - shape.HorizontalRadius;
        var maxZ = Math.Max(previous.Z, center.Z) + shape.HorizontalRadius;

        for (var blockZ = minZ >> SectionBlockPos.Log; blockZ <= maxZ >> SectionBlockPos.Log; blockZ++)
        {
            for (var blockY = minY >> SectionBlockPos.Log; blockY <= maxY >> SectionBlockPos.Log; blockY++)
            {
                var count = 0;

                for (var z = Math.Max(minZ, blockZ << SectionBlockPos.Log); z <= Math.Min(maxZ, (blockZ << SectionBlockPos.Log) + SectionBlockPos.BlockMask); z++)
                {
                    for (var y = Math.Max(minY, blockY << SectionBlockPos.Log); y <= Math.Min(maxY, (blockY << SectionBlockPos.Log) + SectionBlockPos.BlockMask); y++)
                    {
                        var wasInside = shape.TryGetRow(previous, y, z, out var previousMinX, out var previousMaxX);
                        var isInside = shape.TryGetRow(center, y, z, out var minX, out var maxX);

                        if (isInside)
                        {
                            count = AddRunsOutside(runs, count, entering, minX, maxX, wasInside, previousMinX, previousMaxX, y, z);
                        }

                        if (wasInside && leaving != RowOperation.None)
                        {
                            count = AddRunsOutside(runs, count, leaving, previousMinX, previousMaxX, isInside, minX, maxX, y, z);
                        }
                    }
                }

                ApplyRuns(runs[..count], blockY, blockZ);
            }
        }
    }

    /// <summary>
    ///     Adds the part of the row from <paramref name="minX"/> to <paramref name="maxX"/> outside the excluded interval, when there is one, and returns the new run count.
    /// </summary>
    private static int AddRunsOutside(Span<RowRun> runs, int count, RowOperation operation, int minX, int maxX, bool hasExcluded, int excludedMinX, int excludedMaxX, int y, int z)
    {
        if (!hasExcluded || excludedMaxX < minX || excludedMinX > maxX)
        {
            runs[count++] = new RowRun(minX, maxX, y, z, operation);
            return count;
        }

        if (minX < excludedMinX)
        {
            runs[count++] = new RowRun(minX, excludedMinX - 1, y, z, operation);
        }

        if (maxX > excludedMaxX)
        {
            runs[count++] = new RowRun(excludedMaxX + 1, maxX, y, z, operation);
        }

        return count;
    }

    /// <summary>
    ///     Applies the runs of one row of blocks, one block at a time, so each block the runs touch is looked up once.
    /// </summary>
    private void ApplyRuns(ReadOnlySpan<RowRun> runs, int blockY, int blockZ)
    {
        if (runs.IsEmpty)
        {
            return;
        }

        var minX = int.MaxValue;
        var maxX = int.MinValue;

        foreach (var run in runs)
        {
            minX = Math.Min(minX, run.MinX);
            maxX = Math.Max(maxX, run.MaxX);
        }

        var firstBlockX = minX >> SectionBlockPos.Log;
        var blockCount = (maxX >> SectionBlockPos.Log) - firstBlockX + 1;

        // A move's runs sit at the two ends of the row, so only the blocks some run touches are visited:
        Span<bool> touched = blockCount <= MaxStackBlocksPerRow ? stackalloc bool[blockCount] : new bool[blockCount];

        foreach (var run in runs)
        {
            touched[((run.MinX >> SectionBlockPos.Log) - firstBlockX)..((run.MaxX >> SectionBlockPos.Log) - firstBlockX + 1)].Fill(true);
        }

        for (var offset = 0; offset < blockCount; offset++)
        {
            if (touched[offset])
            {
                ApplyRunsToBlock(runs, firstBlockX + offset, blockY, blockZ);
            }
        }
    }

    private void ApplyRunsToBlock(ReadOnlySpan<RowRun> runs, int blockX, int blockY, int blockZ)
    {
        var blockMinX = blockX << SectionBlockPos.Log;
        var blockMaxX = blockMinX + SectionBlockPos.BlockMask;
        var blockKey = SectionBlockPos.Pack(blockX, blockY, blockZ);
        var fetched = false;
        var released = false;
        var view = default(SectionBlockView);

        foreach (var run in runs)
        {
            var minX = Math.Max(run.MinX, blockMinX);
            var maxX = Math.Min(run.MaxX, blockMaxX);

            if (minX > maxX)
            {
                continue;
            }

            if (!fetched)
            {
                // Only a claim creates a block, since a released or wanted section is already claimed:
                view = run.Operation == RowOperation.Claim ? GetOrAddBlock(blockKey) : _blockStorage.Get(_blocks[blockKey]);
                fetched = true;
            }

            released |= run.Operation == RowOperation.Release;
            ApplyRun(view, run.Operation, minX, maxX, run.Y, run.Z);
        }

        if (released)
        {
            FreeBlockIfEmpty(blockKey, view);
        }
    }

    private void ApplyRun(SectionBlockView view, RowOperation operation, int minX, int maxX, int y, int z)
    {
        var rowIndex = SectionBlockPos.GetLocalIndex(0, y, z);

        switch (operation)
        {
            case RowOperation.None:
                break;
            case RowOperation.Claim:
                for (var x = minX; x <= maxX; x++)
                {
                    AddTicketReference(view, rowIndex | (x & SectionBlockPos.BlockMask));
                }

                break;
            case RowOperation.Release:
                for (var x = minX; x <= maxX; x++)
                {
                    RemoveTicketReference(view, rowIndex | (x & SectionBlockPos.BlockMask), new SectionPos(x, y, z));
                }

                break;
            case RowOperation.Want:
                for (var x = minX; x <= maxX; x++)
                {
                    MarkWanted(view, rowIndex | (x & SectionBlockPos.BlockMask), new SectionPos(x, y, z));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static void AddTicketReference(SectionBlockView view, int index)
    {
        if (view.TicketCounts[index]++ == 0)
        {
            view.Claimed |= 1UL << index;
        }
    }

    private void RemoveTicketReference(SectionBlockView view, int index, SectionPos position)
    {
        var bit = 1UL << index;

        Debug.Assert(view.TicketCounts[index] > 0, $"Released a ticket reference on section ({position.X}, {position.Y}, {position.Z}), which no ticket claimed.");

        if (--view.TicketCounts[index] > 0)
        {
            return;
        }

        view.Claimed &= ~bit;

        if ((view.Ready & bit) != 0)
        {
            // A queued section claimed again before its turn is skipped then, so it is queued at most once:
            if ((view.UnloadQueued & bit) == 0)
            {
                view.UnloadQueued |= bit;
                _unloadQueue.Enqueue(position.GetKey());
            }

            return;
        }

        // A generating section keeps its entry until the generation completes and the harvest discards it, and any other was at most pending:
        if ((view.Generating & bit) == 0)
        {
            _pending.Remove(position.GetKey());
        }
    }

    private void MarkWanted(SectionBlockView view, int index, SectionPos position)
    {
        var bit = 1UL << index;

        Debug.Assert((view.Claimed & bit) != 0, $"Section ({position.X}, {position.Y}, {position.Z}) entered a load shape without being claimed by the keep shape around it.");

        if (((view.Ready | view.Generating) & bit) == 0)
        {
            _pending.Add(position.GetKey());
        }
    }

    private void UnloadUnticketedLoads()
    {
        foreach (var key in _unticketedLoads)
        {
            var position = new SectionPos(key);

            if (!TryGetBlock(position, out var blockKey, out var view))
            {
                continue;
            }

            var index = SectionBlockPos.GetLocalIndex(position);
            var bit = 1UL << index;

            // A section a ticket claimed and released since is in the unload queue, which owns its unload now:
            if (view.TicketCounts[index] > 0 || (view.Ready & bit) == 0 || (view.UnloadQueued & bit) != 0)
            {
                continue;
            }

            DestroySection(position, blockKey, view, bit);
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
            var position = new SectionPos(key);
            var found = TryGetBlock(position, out var blockKey, out var view);
            var index = SectionBlockPos.GetLocalIndex(position);
            var bit = 1UL << index;

            Debug.Assert(
                found && (view.UnloadQueued & bit) != 0 && (view.Ready & bit) != 0,
                "Only the unload queue removes a queued entry, and a queued entry stays loaded until then."
            );

            view.UnloadQueued &= ~bit;

            if (view.TicketCounts[index] > 0)
            {
                continue;
            }

            DestroySection(position, blockKey, view, bit);
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

            var position = new SectionPos(inFlight.Key);

            if (TryGetBlock(position, out var blockKey, out var view))
            {
                var localIndex = SectionBlockPos.GetLocalIndex(position);
                var bit = 1UL << localIndex;

                // An entry that is no longer generating into this slot was unloaded or loaded synchronously since, so the result is dropped.
                // The slot tells the generations apart, because it stays allocated until its own harvest:
                if ((view.Generating & bit) != 0 && view.Slots[localIndex] == inFlight.Slot)
                {
                    if (view.TicketCounts[localIndex] > 0)
                    {
                        view.Slots[localIndex] = _terrain.CreateCopy(_executor.GetResult(inFlight.Slot)).SlotIndex;
                        view.Generating &= ~bit;
                        view.Ready |= bit;
                    }
                    else
                    {
                        view.Generating &= ~bit;
                        FreeBlockIfEmpty(blockKey, view);
                    }
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
            var position = new SectionPos(key);

            if (!TryGetBlock(position, out _, out var view))
            {
                continue;
            }

            var index = SectionBlockPos.GetLocalIndex(position);
            var bit = 1UL << index;

            // A section that left every keep shape or loaded since the rebuild went stale in the queue:
            if ((view.Claimed & bit) == 0 || ((view.Ready | view.Generating) & bit) != 0)
            {
                continue;
            }

            _pending.Remove(key);

            if (IsSky(position))
            {
                view.Slots[index] = WriteSky(position).SlotIndex;
                view.Ready |= bit;
                continue;
            }

            var slot = _executor.Allocate(position);
            var job = pool.CreateJob();
            var published = _scheduler.Publish(job, _providerId, slot);

            view.Slots[index] = slot;
            view.Generating |= bit;

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

    /// <summary>
    ///     The storage of the block holding the section. False when no section of that block has an entry.
    /// </summary>
    private bool TryGetBlock(SectionPos position, out ulong blockKey, out SectionBlockView view)
    {
        blockKey = SectionBlockPos.FromSection(position).GetKey();

        if (_blocks.TryGetValue(blockKey, out var block))
        {
            view = _blockStorage.Get(block);
            return true;
        }

        view = default;
        return false;
    }

    internal bool TryGetBlock(ulong blockKey, out SectionBlock block)
    {
        return _blocks.TryGetValue(blockKey, out block);
    }

    private SectionBlockView GetOrAddBlock(ulong blockKey)
    {
        ref var block = ref CollectionsMarshal.GetValueRefOrAddDefault(_blocks, blockKey, out var exists);

        if (!exists)
        {
            block = _blockStorage.Allocate();
            _blockVersion++;
        }

        return _blockStorage.Get(block);
    }

    private void FreeBlockIfEmpty(ulong blockKey, SectionBlockView view)
    {
        if (view.Occupied != 0)
        {
            return;
        }

        _blocks.Remove(blockKey, out var block);
        _blockStorage.Free(block);
        _blockVersion++;
    }

    /// <summary>
    ///     Destroys a loaded section that no ticket holds, and frees its block when nothing else in it has an entry.
    /// </summary>
    private void DestroySection(SectionPos position, ulong blockKey, SectionBlockView view, ulong bit)
    {
        _terrain.Destroy(position);
        view.Ready &= ~bit;
        FreeBlockIfEmpty(blockKey, view);
    }

    internal SectionStatus GetStatus(SectionBlock block, ulong bit)
    {
        if ((_blockStorage.GetMask(block, SectionBlockView.ReadyMask) & bit) != 0)
        {
            return SectionStatus.Ready;
        }

        return (_blockStorage.GetMask(block, SectionBlockView.GeneratingMask) & bit) != 0 ? SectionStatus.Generating : SectionStatus.None;
    }

    /// <summary>
    ///     The section's handle when it is loaded, rebuilt from the arena slot its block holds.
    /// </summary>
    internal bool TryGetSection(SectionBlock block, SectionPos position, out Section section)
    {
        var index = SectionBlockPos.GetLocalIndex(position);

        if ((_blockStorage.GetMask(block, SectionBlockView.ReadyMask) & (1UL << index)) != 0)
        {
            section = new Section(_terrain, position, _blockStorage.GetSlot(block, index));
            return true;
        }

        section = default;
        return false;
    }

    private static ulong GetBit(SectionPos position)
    {
        return 1UL << SectionBlockPos.GetLocalIndex(position);
    }
}

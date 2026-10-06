using System.Diagnostics;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World.Generation;
using WinterGame.World.Level.Loading;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Level.Residency;
using WinterGame.World.Storage;

namespace WinterGame.World.Level;

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
/// <remarks>
///     Every transition of a section goes through one <see cref="SectionTable"/>.
///     <see cref="TicketResidency"/> decides what stays loaded, <see cref="SectionLoader"/> loads what the tickets want, <see cref="MeshReadiness"/> decides what is meshed, and <see cref="SectionVoxelOps"/> moves voxels in and out of bricks.
///     The level orders them in <see cref="Update"/>.
/// </remarks>
public sealed class WorldLevel : IDisposable
{
    /// <summary>
    ///     Sections past a ticket's shape that stay loaded once loaded, so a loader moving back and forth across a boundary does not reload it.
    /// </summary>
    public const int KeepMargin = 1;

    /// <summary>
    ///     Released sections one <see cref="Update"/> destroys by default.
    /// </summary>
    public const int DefaultUnloadsPerUpdate = 512;

    private readonly JobScheduler _scheduler;
    private readonly SectionBlockMap _blocks = new();
    private readonly MeshReadiness _meshReadiness;
    private readonly SectionTable _table;
    private readonly TicketResidency _residency;
    private readonly SectionLoader _loader;

    private bool _disposed;

    /// <summary>
    ///     Creates a level and registers its generation executor.
    ///     <paramref name="meshing"/> has to be the description the sections are meshed with, since its window decides which sections a write meshes again, and is the default one when null.
    /// </summary>
    public WorldLevel(
        JobScheduler scheduler,
        TerrainGraph graph,
        int slotCount = SectionGenerationExecutor.DefaultSlotCount,
        int unloadsPerUpdate = DefaultUnloadsPerUpdate,
        SectionMeshingDescription? meshing = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(unloadsPerUpdate, 1);

        if (scheduler.IsStarted)
        {
            throw new InvalidOperationException("The level registers its generation executor, so it has to be created before the scheduler starts.");
        }

        _scheduler = scheduler;
        _meshReadiness = new MeshReadiness(_blocks, (meshing ?? new SectionMeshingDescription()).GetWindow());
        _table = new SectionTable(_blocks, _meshReadiness);
        _residency = new TicketResidency(_table, unloadsPerUpdate);
        _loader = new SectionLoader(_table, scheduler, graph, slotCount);
    }

    /// <summary>
    ///     The inclusive box of samples a section's mesh reads, relative to its origin, as the level was told at construction.
    /// </summary>
    public BoundingBox3i MeshWindow => _meshReadiness.MeshWindow;

    /// <summary>
    ///     Released sections waiting to be destroyed, the backlog a long move leaves behind.
    /// </summary>
    public int UnloadQueueCount => _residency.UnloadQueueCount;

    internal SectionTable Table => _table;

    internal int PendingCount => _loader.PendingCount;

    /// <summary>
    ///     The sections in the load queue, a section once for each of its entries.
    /// </summary>
    internal IEnumerable<SectionPos> GetQueuedSections()
    {
        return _loader.GetQueuedSections();
    }

    internal int InFlightCount => _loader.InFlightCount;

    internal int SectionEntryCount => _blocks.CountOccupied();

    internal int NewlyReadyCount => _blocks.CountSet(SectionBlockView.NewlyReadyMask);

    internal int GetTicketCount(SectionPos position)
    {
        return _blocks.TryGet(position, out _, out var view) ? view.TicketCounts[SectionBlockPos.GetLocalIndex(position)] : 0;
    }

    internal bool IsUnloadQueued(SectionPos position)
    {
        return _blocks.TryGet(position, out _, out var view) && view.IsUnloadQueued(SectionBlockPos.GetLocalIndex(position));
    }

    /// <summary>
    ///     The sides of the iso level a ready section's content is on, and the default for a section that is not ready.
    /// </summary>
    internal SectionContentSides GetContentSides(SectionPos position)
    {
        var index = SectionBlockPos.GetLocalIndex(position);

        return _blocks.TryGet(position, out _, out var view) && view.IsReady(index) ? view.Slots[index].ContentSides : default;
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

        _residency.Add(ticket);
        _loader.InvalidateLoadQueue();

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

        if (ticket.Center == center)
        {
            return;
        }

        _residency.Move(ticket, center);
        _loader.InvalidateLoadQueue();
    }

    /// <summary>
    ///     Removes a ticket, releasing every section of its keep shape. The sections no other ticket keeps are unloaded by the following ticks.
    /// </summary>
    public void RemoveTicket(LevelTicket ticket)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateTicket(ticket);

        _residency.Remove(ticket);
        ticket.Owner = null;
        _loader.InvalidateLoadQueue();
    }

    /// <summary>
    ///     Unloads the previous tick's unticketed loads and up to the budget of released sections, copies finished generations into the level, publishes the nearest wanted sections into the free executor slots, and reports the mesh changes.
    /// </summary>
    public void Update()
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        _scheduler.Pool.ThrowIfFaulted();

        // Unloads come before the harvest, so the harvest reuses their storage:
        _residency.UnloadUnticketedLoads();
        _residency.UnloadReleasedSections();
        _loader.Update(_residency.Tickets);

        // What the harvest loaded is reported even when a refused publish held back new ones:
        _meshReadiness.ReportCompletedNeighborhoods();
    }

    /// <summary>
    ///     Moves the mesh changes since the last call into <see cref="destination"/>, in the order they happened, so a consumer can take them at any rate.
    ///     A section is reported to mesh once it and its 26 neighbors are ready and it holds a surface, and its mesh is reported to retire when it is destroyed.
    ///     A neighbor unloading retires nothing, and the section is not reported again when the neighbor is back.
    /// </summary>
    public void TakeMeshChanges(List<SectionMeshChange> destination)
    {
        WgAssert.DebugIsOnGameThread();
        ArgumentNullException.ThrowIfNull(destination);

        _meshReadiness.Take(destination);
    }

    /// <summary>
    ///     Reports a section to mesh again once it and its 26 neighbors are ready, for a consumer that took its Mesh change but found a neighbor unloaded before it could read the window.
    ///     The next <see cref="Update"/> reports it if its neighborhood is complete again by then, and otherwise the update that completes it does.
    ///     A section that is not ready, or not reported, is left alone.
    /// </summary>
    public void ReportMeshAgain(SectionPos position)
    {
        WgAssert.DebugIsOnGameThread();

        _meshReadiness.ReportAgain(position);
    }

    public LevelStats GetStats()
    {
        WgAssert.DebugIsOnGameThread();

        return new LevelStats(_table.Terrain.SectionCount, _loader.InFlightCount, _loader.PendingCount, _residency.UnloadQueueCount);
    }

    public SectionStatus GetStatus(SectionPos position)
    {
        WgAssert.DebugIsOnGameThread();

        return _table.GetStatus(position);
    }

    public bool TryGetSection(SectionPos position, out Section section)
    {
        WgAssert.DebugIsOnGameThread();

        return _table.TryGetSection(position, out section);
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

        var generated = _loader.LoadNow(position);
        var blockKey = SectionBlockPos.FromSection(position).GetKey();
        var view = _blocks.GetOrAdd(blockKey);
        var index = SectionBlockPos.GetLocalIndex(position);

        _table.MarkReady(blockKey, view, index, generated.Section.SlotIndex, generated.ContentSides);

        if (view.TicketCounts[index] == 0)
        {
            _residency.AddUnticketedLoad(position);
        }

        return generated.Section;
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

        var accessor = new FastLevelAccessor(this);
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
                        SectionVoxelOps.ReadSection(section, clipMin - origin, clipMax - origin, origin - min, content, material);
                        continue;
                    }

                    complete = false;
                    SectionVoxelOps.FillBox(content, clipMin - min, clipMax - min, VoxelChannel.Content.GetClearValue());
                    SectionVoxelOps.FillBox(material, clipMin - min, clipMax - min, VoxelChannel.Material.GetClearValue());
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
        return new FastLevelAccessor(this).ReadPoint(position, out content, out material);
    }

    /// <summary>
    ///     Writes both channels of the inclusive box into the loaded sections, the reverse of <see cref="ReadRange"/>, so an edit reads a box, changes it and writes it back.
    ///     Nothing is written and the result is false when the box touches a section that is not ready.
    ///     A brick left holding one value is stored uniform, and a uniform brick written with its own value is left alone, so a write that changes nothing keeps a shared section shared.
    ///     Every ready section whose mesh window holds a changed brick is reported as an <see cref="SectionMeshChangeKind.Edit"/> when it has a mesh, or when its neighborhood is ready and it now holds a surface.
    ///     Both sources are sized <c>maxInclusive - min + 1</c> on every axis.
    /// </summary>
    public bool WriteRange(Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRange(min, maxInclusive, content, material);

        var accessor = new FastLevelAccessor(this);
        var sectionMin = min >> SectionPos.Log;
        var sectionMax = maxInclusive >> SectionPos.Log;

        // Every section is checked first, so a write is all or nothing:
        for (var sectionZ = sectionMin.Z; sectionZ <= sectionMax.Z; sectionZ++)
        {
            for (var sectionY = sectionMin.Y; sectionY <= sectionMax.Y; sectionY++)
            {
                for (var sectionX = sectionMin.X; sectionX <= sectionMax.X; sectionX++)
                {
                    if (!accessor.TryGetSection(new SectionPos(sectionX, sectionY, sectionZ), out _))
                    {
                        return false;
                    }
                }
            }
        }

        var changedMin = new Vector3i(int.MaxValue);
        var changedMax = new Vector3i(int.MinValue);

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

                    accessor.TryGetSection(position, out var section);

                    if (SectionVoxelOps.WriteSection(section, clipMin - origin, clipMax - origin, origin - min, content, material, ref changedMin, ref changedMax))
                    {
                        var found = _blocks.TryGet(position, out _, out var view);

                        Debug.Assert(found, "A section the write found ready has a block.");

                        view.SetContentSides(SectionBlockPos.GetLocalIndex(position), SectionContentSides.FromSection(section));
                    }
                }
            }
        }

        if (changedMin.X <= changedMax.X)
        {
            _meshReadiness.ReportEditedSections(changedMin, changedMax);
        }

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
        _loader.Dispose();
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
}

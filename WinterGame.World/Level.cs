using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    private struct SectionEntry
    {
        public SectionPos Position;

        /// <summary>
        ///     Tickets whose keep shape covers the section.
        /// </summary>
        public int TicketCount;

        public SectionStatus Status;

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

    private readonly SectionArena _terrain = new();
    private readonly Dictionary<ulong, SectionEntry> _sections = [];

    /// <summary>
    ///     Sections a ticket covers that are neither generating nor loaded.
    /// </summary>
    private readonly HashSet<ulong> _pending = [];

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
    public Level(JobScheduler scheduler, TerrainGraph graph, int slotCount = SectionGenerationExecutor.DefaultSlotCount)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(graph);

        if (scheduler.IsStarted)
        {
            throw new InvalidOperationException("The level registers its generation executor, so it has to be created before the scheduler starts.");
        }

        _scheduler = scheduler;
        _executor = new SectionGenerationExecutor(graph, scheduler.ThreadCount, slotCount);
        _providerId = scheduler.RegisterExecutor(_executor);
        _skyMinimumY = graph.MaxSurfaceY + WorldConstants.VoxelSize;
    }

    internal int PendingCount => _pending.Count;

    internal int InFlightCount => _inFlight.Count;

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
        AdjustKeepShape(ticket, center, 1);
        _loadQueueDirty = true;

        return ticket;
    }

    /// <summary>
    ///     Moves a ticket, claiming the sections that entered its keep shape and releasing the ones that left it.
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

        var keepHorizontal = ticket.HorizontalRadius + KeepMargin;
        var keepVertical = ticket.VerticalRadius + KeepMargin;

        var minX = Math.Min(previous.X, center.X) - keepHorizontal;
        var maxX = Math.Max(previous.X, center.X) + keepHorizontal;
        var minY = Math.Min(previous.Y, center.Y) - keepVertical;
        var maxY = Math.Max(previous.Y, center.Y) + keepVertical;
        var minZ = Math.Min(previous.Z, center.Z) - keepHorizontal;
        var maxZ = Math.Max(previous.Z, center.Z) + keepHorizontal;

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var wasInside = IsInsideShape(previous, x, y, z, keepHorizontal, keepVertical);
                    var isInside = IsInsideShape(center, x, y, z, keepHorizontal, keepVertical);

                    if (wasInside == isInside)
                    {
                        continue;
                    }

                    if (isInside)
                    {
                        AddTicketReference(new SectionPos(x, y, z));
                    }
                    else
                    {
                        RemoveTicketReference(new SectionPos(x, y, z));
                    }
                }
            }
        }

        _loadQueueDirty = true;
    }

    /// <summary>
    ///     Removes a ticket, unloading every section no other ticket keeps.
    /// </summary>
    public void RemoveTicket(LevelTicket ticket)
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateTicket(ticket);

        AdjustKeepShape(ticket, ticket.Center, -1);
        _tickets.Remove(ticket);
        ticket.Owner = null;
        _loadQueueDirty = true;
    }

    /// <summary>
    ///     Unloads the previous tick's unticketed loads, copies finished generations into the level, and publishes the nearest wanted sections into the free executor slots.
    /// </summary>
    public void Update()
    {
        WgAssert.DebugIsOnGameThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        _scheduler.Pool.ThrowIfFaulted();

        UnloadUnticketedLoads();
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
    ///     A section no ticket keeps is unloaded on the next <see cref="Update"/>, so a caller that needs it for longer needs a ticket.
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInsideShape(SectionPos center, int x, int y, int z, int horizontalRadius, int verticalRadius)
    {
        var deltaX = x - center.X;
        var deltaZ = z - center.Z;

        return deltaX * deltaX + deltaZ * deltaZ <= horizontalRadius * horizontalRadius && Math.Abs(y - center.Y) <= verticalRadius;
    }

    private void AdjustKeepShape(LevelTicket ticket, SectionPos center, int delta)
    {
        var keepHorizontal = ticket.HorizontalRadius + KeepMargin;
        var keepVertical = ticket.VerticalRadius + KeepMargin;

        for (var z = center.Z - keepHorizontal; z <= center.Z + keepHorizontal; z++)
        {
            for (var y = center.Y - keepVertical; y <= center.Y + keepVertical; y++)
            {
                for (var x = center.X - keepHorizontal; x <= center.X + keepHorizontal; x++)
                {
                    if (!IsInsideShape(center, x, y, z, keepHorizontal, keepVertical))
                    {
                        continue;
                    }

                    if (delta > 0)
                    {
                        AddTicketReference(new SectionPos(x, y, z));
                    }
                    else
                    {
                        RemoveTicketReference(new SectionPos(x, y, z));
                    }
                }
            }
        }
    }

    private void AddTicketReference(SectionPos position)
    {
        var key = position.GetKey();
        ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_sections, key, out var exists);

        if (!exists)
        {
            entry.Position = position;
        }

        entry.TicketCount++;

        if (entry.TicketCount == 1 && entry.Status == SectionStatus.None)
        {
            _pending.Add(key);
        }
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
                _terrain.Destroy(position);
                _sections.Remove(key);
                break;
            case SectionStatus.Generating:
                // The entry stays until the generation completes, and the harvest discards it then.
                break;
            default:
                throw new InvalidOperationException($"Section status {entry.Status} is not handled.");
        }
    }

    private void UnloadUnticketedLoads()
    {
        foreach (var key in _unticketedLoads)
        {
            ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_sections, key);

            if (Unsafe.IsNullRef(ref entry) || entry.TicketCount > 0 || entry.Status != SectionStatus.Ready)
            {
                continue;
            }

            _terrain.Destroy(entry.Position);
            _sections.Remove(key);
        }

        _unticketedLoads.Clear();
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
        }

        _loadQueueDirty = false;
    }

    /// <summary>
    ///     The squared distance from the section to the nearest ticket whose shape holds it. False when it is only in some ticket's keep margin.
    /// </summary>
    private bool TryGetLoadPriority(SectionPos position, out long priority)
    {
        priority = long.MaxValue;

        foreach (var ticket in _tickets)
        {
            var center = ticket.Center;

            if (!IsInsideShape(center, position.X, position.Y, position.Z, ticket.HorizontalRadius, ticket.VerticalRadius))
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
        var section = _terrain.Create(position);

        for (var brick = 0; brick < Section.Bricks; brick++)
        {
            section.SetUniform(VoxelChannel.Content, brick, 0);
            section.SetUniform(VoxelChannel.Material, brick, SurfaceMaterial.Null);
        }

        return section;
    }
}

using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Level.Residency;
using WinterGame.World.Storage;

// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator

namespace WinterGame.World.Level.Loading;

/// <summary>
///     Loads the sections the tickets' load shapes hold: generated off-thread nearest first, one per free executor slot per update, and a section entirely above its column's surface written as uniform air without a job.
/// </summary>
internal sealed class SectionLoader
{
    /// <summary>
    ///     Walks a ticket's load shape, queuing its sections that are neither generating nor loaded.
    /// </summary>
    private readonly struct QueueVisitor(SectionLoader loader, SectionPos center) : ITicketShapeVisitor
    {
        public void VisitBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<TicketShapeRun> runs)
        {
            loader.QueueBlock(blockX, blockY, blockZ, runs, center);
        }
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

    private readonly SectionTable _table;
    private readonly JobScheduler _scheduler;
    private readonly SectionGenerationExecutor _executor;
    private readonly int _providerId;

    /// <summary>
    ///     The lowest world height in meters at which a section is known to be air, one sample above the highest surface, since content only reaches zero one sample past the surface.
    /// </summary>
    private readonly float _skyMinimumY;

    /// <summary>
    ///     The lowest section that is air in every column, the first whose lowest sample is at or above <see cref="_skyMinimumY"/>.
    /// </summary>
    public readonly int LowestSkySectionY;

    /// <summary>
    ///     Evaluates <see cref="TerrainGraph.SurfaceBoundGraph"/> on the game thread.
    /// </summary>
    private readonly DensitySampler _surfaceBound;

    // The corner lattice's spacing in samples, whose columns bound a section column's surface:
    private readonly int _cellX;
    private readonly int _cellZ;

    /// <summary>
    ///     Each section column's lowest sky section, computed when the column is first asked about and kept, about 28 bytes per column ever visited.
    /// </summary>
    private readonly Dictionary<ulong, int> _lowestSkySectionYs = [];

    /// <summary>
    ///     The sections inside some ticket's load shape that are neither generating nor loaded, by squared distance to that ticket's center.
    ///     Rebuilt from the load shapes after a ticket changes. A section two load shapes hold is queued once by each, so the nearer entry comes first, and entries that went stale since are skipped when dequeued.
    /// </summary>
    private readonly PriorityQueue<ulong, long> _loadQueue = new();

    private readonly List<InFlightGeneration> _inFlight = [];

    private bool _loadQueueDirty;

    /// <summary>
    ///     Creates the loader and registers its generation executor, so the scheduler must not be started yet.
    /// </summary>
    public SectionLoader(SectionTable table, JobScheduler scheduler, TerrainGraph graph, int slotCount)
    {
        _table = table;
        _scheduler = scheduler;
        _executor = new SectionGenerationExecutor(graph, scheduler.ThreadCount, slotCount);
        _providerId = scheduler.RegisterExecutor(_executor);
        _skyMinimumY = graph.MaxSurfaceY + WorldConstants.VoxelSize;
        LowestSkySectionY = SectionYAtOrAbove(_skyMinimumY);
        _surfaceBound = new DensitySampler(graph.SurfaceBoundGraph);
        _cellX = graph.Graph.CellX;
        _cellZ = graph.Graph.CellZ;
    }

    public int PendingCount => _loadQueue.Count;

    public int InFlightCount => _inFlight.Count;

    /// <summary>
    ///     The sections in the load queue, a section once for each of its entries.
    /// </summary>
    public IEnumerable<SectionPos> GetQueuedSections()
    {
        foreach (var (key, _) in _loadQueue.UnorderedItems)
        {
            yield return new SectionPos(key);
        }
    }

    /// <summary>
    ///     Rebuilds the load queue on the next update that publishes, after a ticket was added, moved or removed.
    /// </summary>
    public void InvalidateLoadQueue()
    {
        _loadQueueDirty = true;
    }

    /// <summary>
    ///     Copies finished generations into the table, then publishes the nearest wanted sections into the free executor slots.
    /// </summary>
    public void Update(IReadOnlyList<LevelTicket> tickets)
    {
        HarvestGenerations();

        // A publish refused again holds back new ones:
        if (!RepublishRefusedGenerations())
        {
            return;
        }

        if (_loadQueueDirty)
        {
            RebuildLoadQueue(tickets);
        }

        PublishGenerations();
    }

    /// <summary>
    ///     Generates the section on the calling thread straight into the table's arena, or writes it as air when it is above the surface.
    ///     The caller marks it ready, which makes the harvest drop a generation in flight for it.
    /// </summary>
    public GeneratedSection LoadNow(SectionPos position)
    {
        return IsSky(position) ? WriteSky(position) : _executor.GenerateNow(position, _table.Terrain);
    }

    /// <summary>
    ///     Waits for every generation in flight and returns its slot and job.
    /// </summary>
    public void Dispose()
    {
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

            if (_table.Blocks.TryGet(position, out var blockKey, out var view))
            {
                var localIndex = SectionBlockPos.GetLocalIndex(position);

                // An entry that is no longer generating into this slot was unloaded or loaded synchronously since, so the result is dropped.
                // The slot tells the generations apart, because it stays allocated until its own harvest:
                if (view.IsGenerating(localIndex) && view.Slots[localIndex].Index == inFlight.Slot)
                {
                    if (view.TicketCounts[localIndex] > 0)
                    {
                        var generated = _executor.GetResult(inFlight.Slot);

                        _table.MarkReady(blockKey, view, localIndex, _table.Terrain.CreateCopy(generated.Section).SlotIndex, generated.ContentSides);
                    }
                    else
                    {
                        _table.DropGeneration(blockKey, view, localIndex);
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

    /// <summary>
    ///     Queues the wanted sections of every ticket that is not satisfied, and marks satisfied each one whose load shape had none.
    /// </summary>
    private void RebuildLoadQueue(IReadOnlyList<LevelTicket> tickets)
    {
        _loadQueue.Clear();

        for (var index = 0; index < tickets.Count; index++)
        {
            var ticket = tickets[index];

            if (ticket.IsSatisfied)
            {
                continue;
            }

            var queued = _loadQueue.Count;

            ticket.LoadShape.Walk(ticket.Center, true, new QueueVisitor(this, ticket.Center));
            ticket.IsSatisfied = _loadQueue.Count == queued;
        }

        _loadQueueDirty = false;
    }

    /// <summary>
    ///     Queues the wanted sections of one block's part of a load shape. A load shape lies inside its ticket's keep shape, so its block is always in the map.
    /// </summary>
    private void QueueBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<TicketShapeRun> runs, SectionPos center)
    {
        var blockMinX = blockX << SectionBlockPos.Log;
        var blockMaxX = blockMinX + SectionBlockPos.BlockMask;
        var view = _table.Blocks.Get(SectionBlockPos.Pack(blockX, blockY, blockZ));

        foreach (var run in runs)
        {
            var minX = Math.Max(run.MinX, blockMinX);
            var maxX = Math.Min(run.MaxX, blockMaxX);

            if (minX <= maxX)
            {
                QueueRow(view, SectionBlockPos.GetLocalIndex(0, run.Y, run.Z), minX, maxX, run.Y, run.Z, center);
            }
        }
    }

    /// <summary>
    ///     Queues the sections of one row of one block that are neither generating nor loaded, by squared distance to <paramref name="center"/>.
    /// </summary>
    private void QueueRow(SectionBlockView view, int rowIndex, int minX, int maxX, int y, int z, SectionPos center)
    {
        var row = ((1UL << (maxX - minX + 1)) - 1) << (rowIndex | (minX & SectionBlockPos.BlockMask));

        Debug.Assert((row & ~view.GetMask(SectionBlockView.ClaimedMask)) == 0, $"A section of row ({y}, {z}) is in a load shape without being claimed by the keep shape around it.");

        var wanted = row & ~(view.GetMask(SectionBlockView.ReadyMask) | view.GetMask(SectionBlockView.GeneratingMask));
        var blockMinX = minX & ~SectionBlockPos.BlockMask;
        long deltaY = y - center.Y;
        long deltaZ = z - center.Z;
        var distanceYZ = deltaY * deltaY + deltaZ * deltaZ;

        while (wanted != 0)
        {
            var x = blockMinX | (BitOperations.TrailingZeroCount(wanted) & SectionBlockPos.BlockMask);
            long deltaX = x - center.X;

            wanted &= wanted - 1;
            _loadQueue.Enqueue(new SectionPos(x, y, z).GetKey(), deltaX * deltaX + distanceYZ);
        }
    }

    private void PublishGenerations()
    {
        var pool = _scheduler.Pool;

        while (_executor.FreeSlotCount > 0 && _loadQueue.TryDequeue(out var key, out _))
        {
            var position = new SectionPos(key);

            if (!_table.Blocks.TryGet(position, out var blockKey, out var view))
            {
                continue;
            }

            var index = SectionBlockPos.GetLocalIndex(position);

            // A section that left every keep shape, loaded, or was taken from another load shape's entry since the rebuild went stale in the queue:
            if (!view.IsClaimed(index) || view.IsReadyOrGenerating(index))
            {
                continue;
            }

            if (IsSky(position))
            {
                var sky = WriteSky(position);

                _table.MarkReady(blockKey, view, index, sky.Section.SlotIndex, sky.ContentSides);
                continue;
            }

            var slot = _executor.Allocate(position);
            var job = pool.CreateJob();
            var published = _scheduler.Publish(job, _providerId, slot);

            _table.MarkGenerating(view, index, slot);

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

    /// <summary>
    ///     The lowest section of a section column whose lowest sample is one sample above the column's surface bound.
    ///     Every corner of its cells is then at least a sample out of the ground, so generating it would give content 0 and no material throughout, which is what the loader writes.
    /// </summary>
    public int GetLowestSkySectionY(int sectionX, int sectionZ)
    {
        ref var lowest = ref CollectionsMarshal.GetValueRefOrAddDefault(_lowestSkySectionYs, SectionPos.Pack(sectionX, 0, sectionZ), out var exists);

        if (!exists)
        {
            var minX = SectionPos.SectionToVoxelCoord(sectionX);
            var minZ = SectionPos.SectionToVoxelCoord(sectionZ);
            var bound = float.NegativeInfinity;

            // The section's cells have their corners on these columns, the ones on its far faces included:
            for (var z = 0; z <= SectionPos.SectionSize; z += _cellZ)
            {
                for (var x = 0; x <= SectionPos.SectionSize; x += _cellX)
                {
                    bound = Math.Max(bound, _surfaceBound.Sample(new Vector3i(minX + x, 0, minZ + z), 0));
                }
            }

            lowest = Math.Min(SectionYAtOrAbove(bound + WorldConstants.VoxelSize), LowestSkySectionY);
        }

        return lowest;
    }

    private bool IsSky(SectionPos position)
    {
        return position.Y >= GetLowestSkySectionY(position.X, position.Z);
    }

    /// <summary>
    ///     The lowest section whose lowest sample is at or above a height in meters.
    /// </summary>
    private static int SectionYAtOrAbove(float height)
    {
        return (int)MathF.Ceiling(height / (SectionPos.SectionSize * WorldConstants.VoxelSize));
    }

    private GeneratedSection WriteSky(SectionPos position)
    {
        return new GeneratedSection(_table.Terrain.CreateUniform(position, 0, SurfaceMaterial.Null), SectionContentSides.FromOneSide(ContentSide.Air));
    }
}

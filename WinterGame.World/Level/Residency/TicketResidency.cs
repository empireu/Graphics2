using System.Diagnostics;
using WinterGame.World.Storage;

// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator

namespace WinterGame.World.Level.Residency;

/// <summary>
///     Which sections stay loaded: every section counts the tickets whose keep shape covers it, and a section whose count reaches zero is queued and destroyed on a budget.
///     It never loads anything; the <see cref="Loading.SectionLoader"/> loads what the tickets' load shapes hold.
/// </summary>
internal sealed class TicketResidency(SectionTable table, int unloadsPerUpdate)
{
    /// <summary>
    ///     Walks a ticket shape, claiming the sections it enters and releasing the ones it leaves.
    /// </summary>
    private readonly struct TicketReferenceVisitor(TicketResidency residency) : ITicketShapeVisitor
    {
        public void VisitBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<TicketShapeRun> runs)
        {
            residency.ApplyRunsToBlock(blockX, blockY, blockZ, runs);
        }
    }

    private readonly List<LevelTicket> _tickets = [];

    /// <summary>
    ///     Loaded sections whose ticket count reached zero, oldest first. Each <see cref="UnloadReleasedSections"/> destroys up to the budget of them, and skips one a ticket has claimed again since.
    /// </summary>
    private readonly Queue<ulong> _unloadQueue = new();

    /// <summary>
    ///     Sections loaded by <see cref="WorldLevel.GetOrLoadSection"/> without a ticket, unloaded on the next <see cref="UnloadUnticketedLoads"/> unless a ticket has claimed them since.
    /// </summary>
    private readonly List<ulong> _unticketedLoads = [];

    public IReadOnlyList<LevelTicket> Tickets => _tickets;

    public int UnloadQueueCount => _unloadQueue.Count;

    /// <summary>
    ///     Adds a ticket and claims every section in its keep shape.
    /// </summary>
    public void Add(LevelTicket ticket)
    {
        _tickets.Add(ticket);
        ticket.KeepShape.Walk(ticket.Center, true, new TicketReferenceVisitor(this));
    }

    /// <summary>
    ///     Moves a ticket, claiming the sections that entered its keep shape and releasing the ones that left it.
    ///     The work is the rows of the box around both placements plus the sections that changed.
    /// </summary>
    public void Move(LevelTicket ticket, SectionPos center)
    {
        var previous = ticket.Center;
        var keepShape = ticket.KeepShape;

        ticket.Center = center;
        ticket.IsSatisfied = false;

        // Placements that cannot overlap are applied whole, since the difference would walk every row between them.
        // The new shape is claimed before the old one is released:
        if (!keepShape.CanOverlap(previous, center))
        {
            keepShape.Walk(center, true, new TicketReferenceVisitor(this));
            keepShape.Walk(previous, false, new TicketReferenceVisitor(this));

            return;
        }

        keepShape.WalkDifference(previous, center, new TicketReferenceVisitor(this));
    }

    /// <summary>
    ///     Removes a ticket, releasing every section of its keep shape.
    /// </summary>
    public void Remove(LevelTicket ticket)
    {
        ticket.KeepShape.Walk(ticket.Center, false, new TicketReferenceVisitor(this));
        _tickets.Remove(ticket);
    }

    /// <summary>
    ///     Remembers a section loaded without a ticket, so the next <see cref="UnloadUnticketedLoads"/> destroys it unless a ticket claimed it since.
    /// </summary>
    public void AddUnticketedLoad(SectionPos position)
    {
        _unticketedLoads.Add(position.GetKey());
    }

    public void UnloadUnticketedLoads()
    {
        foreach (var key in _unticketedLoads)
        {
            var position = new SectionPos(key);

            if (!table.Blocks.TryGet(position, out var blockKey, out var view))
            {
                continue;
            }

            var index = SectionBlockPos.GetLocalIndex(position);

            // A section a ticket claimed and released since is in the unload queue, which owns its unload now:
            if (view.TicketCounts[index] > 0 || !view.IsReady(index) || view.IsUnloadQueued(index))
            {
                continue;
            }

            table.Destroy(position, blockKey, view, index);
        }

        _unticketedLoads.Clear();
    }

    /// <summary>
    ///     Destroys up to the budget of queued sections, oldest first. A section a ticket claimed again since it was queued is dropped from the queue and kept.
    /// </summary>
    public void UnloadReleasedSections()
    {
        var unloaded = 0;

        while (unloaded < unloadsPerUpdate && _unloadQueue.TryDequeue(out var key))
        {
            var position = new SectionPos(key);
            var found = table.Blocks.TryGet(position, out var blockKey, out var view);
            var index = SectionBlockPos.GetLocalIndex(position);

            Debug.Assert(
                found && view.IsUnloadQueued(index) && view.IsReady(index),
                "Only the unload queue removes a queued entry, and a queued entry stays loaded until then."
            );

            view.ClearUnloadQueued(index);

            if (view.TicketCounts[index] > 0)
            {
                continue;
            }

            table.Destroy(position, blockKey, view, index);
            unloaded++;
        }
    }

    private void ApplyRunsToBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<TicketShapeRun> runs)
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
                // Only a claim creates a block, since a released section is already claimed:
                view = run.Entering ? table.Blocks.GetOrAdd(blockKey) : table.Blocks.Get(blockKey);
                fetched = true;
            }

            var rowIndex = SectionBlockPos.GetLocalIndex(0, run.Y, run.Z);

            if (run.Entering)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    AddTicketReference(view, rowIndex | (x & SectionBlockPos.BlockMask));
                }

                continue;
            }

            released = true;

            for (var x = minX; x <= maxX; x++)
            {
                RemoveTicketReference(view, rowIndex | (x & SectionBlockPos.BlockMask), new SectionPos(x, run.Y, run.Z));
            }
        }

        if (released)
        {
            table.Blocks.FreeIfEmpty(blockKey, view);
        }
    }

    private static void AddTicketReference(SectionBlockView view, int index)
    {
        if (view.TicketCounts[index]++ == 0)
        {
            view.SetClaimed(index);
        }
    }

    private void RemoveTicketReference(SectionBlockView view, int index, SectionPos position)
    {
        Debug.Assert(view.TicketCounts[index] > 0, $"Released a ticket reference on section ({position.X}, {position.Y}, {position.Z}), which no ticket claimed.");

        if (--view.TicketCounts[index] > 0)
        {
            return;
        }

        view.ClearClaimed(index);

        // A queued section claimed again before its turn is skipped then, so it is queued at most once.
        // A generating section keeps its entry until the generation completes and the harvest discards it:
        if (view.IsReady(index) && !view.IsUnloadQueued(index))
        {
            view.SetUnloadQueued(index);
            _unloadQueue.Enqueue(position.GetKey());
        }
    }
}

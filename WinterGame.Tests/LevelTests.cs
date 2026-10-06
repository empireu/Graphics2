using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Level;
using WinterGame.World.Level.Loading;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Level.Residency;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class LevelTests
{
    internal static readonly TerrainGraph Graph = TerrainGraph.Build(11, new TerrainConfig());

    /// <summary>
    ///     A level over a scheduler that is never started, so a tick runs its work on the test thread.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public readonly JobScheduler Scheduler = new(new JobSchedulerDescription { ThreadCount = 2 });
        public readonly WorldLevel Level;

        public Harness(int slotCount = SectionGenerationExecutor.DefaultSlotCount, int unloadsPerUpdate = WorldLevel.DefaultUnloadsPerUpdate)
        {
            WgAssert.InitGameThread();
            Level = new WorldLevel(Scheduler, Graph, slotCount, unloadsPerUpdate);
        }

        /// <summary>
        ///     Ticks until an update leaves nothing in flight, which means every loadable section is loaded.
        /// </summary>
        public void TickUntilSettled()
        {
            for (var tick = 0; tick < 1000; tick++)
            {
                Level.Update();

                if (Level.InFlightCount == 0)
                {
                    return;
                }

                Scheduler.Drain();
            }

            throw new InvalidOperationException("The level did not settle.");
        }

        public void Dispose()
        {
            Scheduler.Drain();
            Level.Dispose();
            Scheduler.Dispose();
        }
    }

    [Fact]
    public void Constructor_RejectsAStartedScheduler()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });

        scheduler.Start();

        Assert.Throws<InvalidOperationException>(() => new WorldLevel(scheduler, Graph));
    }

    [Fact]
    public void AddTicket_ClaimsTheKeepShapeWithoutLoadingAnything()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);

        // The keep shape is radius 2 horizontally, which is 13 columns, and 5 sections tall, and nothing is queued before an update:
        Assert.Equal(13 * 5, harness.Level.SectionEntryCount);
        Assert.Equal(0, harness.Level.PendingCount);
        Assert.Equal(0, harness.Level.InFlightCount);
        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
    }

    [Fact]
    public void Update_QueuesTheLoadShapeAndNotTheKeepMargin()
    {
        using var harness = new Harness(slotCount: 1);
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);
        harness.Level.Update();

        // The load shape is 5 columns of 3, and its nearest section took the only slot:
        Assert.Equal(5 * 3 - 1, harness.Level.PendingCount);
        Assert.Equal(1, harness.Level.InFlightCount);
        Assert.Equal(SectionStatus.Generating, harness.Level.GetStatus(center));
    }

    [Fact]
    public void Update_PublishesNoMoreThanTheSlotCount()
    {
        using var harness = new Harness(slotCount: 2);

        harness.Level.AddTicket(SurfaceSection(0, 0), horizontalRadius: 2, verticalRadius: 1);
        harness.Level.Update();

        Assert.Equal(2, harness.Level.InFlightCount);
    }

    [Fact]
    public void Update_LoadsTheNearestSectionFirst()
    {
        using var harness = new Harness(slotCount: 1);
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 2, verticalRadius: 1);
        harness.Level.Update();

        Assert.Equal(SectionStatus.Generating, harness.Level.GetStatus(center));

        harness.Scheduler.Drain();
        harness.Level.Update();

        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(center));

        var neighbors = new[]
        {
            new SectionPos(center.X - 1, center.Y, center.Z),
            new SectionPos(center.X + 1, center.Y, center.Z),
            new SectionPos(center.X, center.Y - 1, center.Z),
            new SectionPos(center.X, center.Y + 1, center.Z),
            new SectionPos(center.X, center.Y, center.Z - 1),
            new SectionPos(center.X, center.Y, center.Z + 1)
        };

        Assert.Single(neighbors, neighbor => harness.Level.GetStatus(neighbor) == SectionStatus.Generating);
    }

    [Fact]
    public void Update_LoadsTheShapeAndLeavesTheKeepMarginUnloaded()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);
        harness.TickUntilSettled();

        foreach (var position in Box(center, 2, 1))
        {
            var deltaX = position.X - center.X;
            var deltaZ = position.Z - center.Z;
            var inShape = deltaX * deltaX + deltaZ * deltaZ <= 1 && position.Y == center.Y;

            Assert.Equal(inShape ? SectionStatus.Ready : SectionStatus.None, harness.Level.GetStatus(position));
        }

        // The keep margin is claimed but never queued, and the 5 loaded sections are no longer queued:
        Assert.Equal(0, harness.Level.PendingCount);
    }

    [Fact]
    public void LoadedSection_MatchesTheGenerator()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.True(harness.Level.TryGetSection(center, out var section));
        Assert.Equal(center, section.Position);

        var reference = GenerateReference(center);

        foreach (var channel in Enum.GetValues<VoxelChannel>())
        {
            for (var brick = 0; brick < Section.Bricks; brick++)
            {
                Assert.Equal(DescribeBrick(reference, channel, brick), DescribeBrick(section, channel, brick));
            }
        }
    }

    [Fact]
    public void Update_WritesASkySectionWithoutAJob()
    {
        using var harness = new Harness();
        var sky = new SectionPos(0, LowestSkySectionY() + 3, 0);

        harness.Level.AddTicket(sky, horizontalRadius: 0, verticalRadius: 0);
        harness.Level.Update();

        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(sky));
        Assert.Equal(0, harness.Level.InFlightCount);
        Assert.True(harness.Level.TryGetSection(sky, out var section));
        AssertUniformAir(section);
    }

    [Fact]
    public void MoveTicket_UnloadsOnlyPastTheKeepMargin()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);
        var left = new SectionPos(center.X - 1, center.Y, center.Z);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(left));

        // The left section is now 2 away, inside the keep margin:
        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 1, center.Y, center.Z));
        harness.TickUntilSettled();

        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(left));

        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 2, center.Y, center.Z));

        // The move only queues the left section, and the next update destroys it:
        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(left));

        harness.Level.Update();

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(left));
        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(center));
    }

    [Fact]
    public void MoveTicket_FarAway_ReleasesTheOldShapeAndClaimsTheNewOne()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);
        harness.TickUntilSettled();

        // The placements cannot overlap, and the rows between them number about 3 * 10^5, so each shape is applied whole:
        var far = new SectionPos(center.X + 100_000, center.Y, center.Z + 100_000);

        harness.Level.MoveTicket(ticket, far);
        harness.Level.Update();

        foreach (var position in Box(center, 2, 1))
        {
            Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
        }

        // The load shape is radius 1 and one section tall, and the update took all 5 of its sections:
        foreach (var position in Box(far, 1, 0).Where(position => IsInsideCylinder(far, position, 1, 0)))
        {
            Assert.NotEqual(SectionStatus.None, harness.Level.GetStatus(position));
        }

        Assert.Equal(0, harness.Level.PendingCount);
    }

    [Fact]
    public void MoveTicket_DiagonalJumpThatStillOverlaps_KeepsTheOverlapLoaded()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);
        var overlap = new SectionPos(center.X + 1, center.Y, center.Z + 1);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 2, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.True(harness.Level.TryGetSection(overlap, out var before));

        // Three sections along both horizontal axes still overlaps the keep disc of radius 3, so only the difference is applied:
        var target = new SectionPos(center.X + 3, center.Y, center.Z + 3);

        harness.Level.MoveTicket(ticket, target);
        harness.Level.Update();

        // The load disc of radius 2 holds 13 sections and none of them was loaded, since the overlap is in the keep margin, so each is either taken by the update or queued:
        var taken = Box(target, 2, 0).Count(position => IsInsideCylinder(target, position, 2, 0) && harness.Level.GetStatus(position) != SectionStatus.None);

        Assert.Equal(13, taken + harness.Level.PendingCount);
        Assert.True(harness.Level.TryGetSection(overlap, out var after));
        Assert.Equal(before.SlotIndex, after.SlotIndex);
        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
    }

    [Fact]
    public void MoveTicket_ToTheSameSection_ChangesNothing()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);

        harness.Level.Update();

        var entries = harness.Level.SectionEntryCount;
        var pending = harness.Level.PendingCount;

        harness.Level.MoveTicket(ticket, center);

        Assert.Equal(entries, harness.Level.SectionEntryCount);
        Assert.Equal(pending, harness.Level.PendingCount);
        Assert.Equal(1, harness.Level.GetTicketCount(center));
    }

    [Fact]
    public void RemoveTicket_UnloadsEverySection()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);
        harness.TickUntilSettled();

        harness.Level.RemoveTicket(ticket);

        // The 5 loaded sections wait in the unload queue until the next update:
        Assert.Equal(0, harness.Level.PendingCount);
        Assert.Equal(5, harness.Level.UnloadQueueCount);

        harness.Level.Update();

        foreach (var position in Box(center, 2, 1))
        {
            Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
        }

        Assert.Equal(0, harness.Level.SectionEntryCount);
    }

    [Fact]
    public void OverlappingTickets_KeepASectionUntilBothAreRemoved()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var first = harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        var second = harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        harness.Level.RemoveTicket(first);

        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(center));

        harness.Level.RemoveTicket(second);
        harness.Level.Update();

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
    }

    [Fact]
    public void ReleasedSection_ClaimedAgainBeforeTheUpdate_KeepsItsData()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.True(harness.Level.TryGetSection(center, out var before));

        harness.Level.RemoveTicket(ticket);

        // Released but not destroyed yet, so it still reads:
        Assert.True(harness.Level.TryGetSection(center, out _));

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.Level.Update();

        Assert.True(harness.Level.TryGetSection(center, out var after));
        Assert.Equal(before.SlotIndex, after.SlotIndex);
        Assert.Equal(0, harness.Level.UnloadQueueCount);
        Assert.Equal(0, harness.Level.InFlightCount);
    }

    [Fact]
    public void Update_UnloadsNoMoreThanTheBudget()
    {
        using var harness = new Harness(unloadsPerUpdate: 2);
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);
        harness.TickUntilSettled();

        var shape = Box(center, 1, 0)
            .Where(position => (position.X - center.X) * (position.X - center.X) + (position.Z - center.Z) * (position.Z - center.Z) <= 1)
            .ToArray();

        harness.Level.RemoveTicket(ticket);

        foreach (var expectedReady in new[] { 5, 3, 1, 0 })
        {
            Assert.Equal(expectedReady, shape.Count(position => harness.Level.GetStatus(position) == SectionStatus.Ready));
            Assert.Equal(expectedReady, harness.Level.UnloadQueueCount);

            harness.Level.Update();
        }
    }

    [Fact]
    public void UnticketedLoad_ClaimedAndReleasedBeforeTheUpdate_IsUnloadedByTheQueue()
    {
        using var harness = new Harness();
        var position = SurfaceSection(0, 0);

        harness.Level.GetOrLoadSection(position);

        var ticket = harness.Level.AddTicket(position, horizontalRadius: 0, verticalRadius: 0);
        harness.Level.RemoveTicket(ticket);
        harness.Level.Update();

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
        Assert.Equal(0, harness.Level.UnloadQueueCount);
        Assert.Equal(0, harness.Level.SectionEntryCount);
    }

    [Fact]
    public void Update_AfterAMove_QueuesOnlyTheMovedLoadShape()
    {
        using var harness = new Harness(slotCount: 1);
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 2, verticalRadius: 0);
        harness.Level.Update();

        // The disc holds 13 sections, and the center took the only slot:
        Assert.Equal(13 - 1, harness.Level.PendingCount);
        Assert.Equal(SectionStatus.Generating, harness.Level.GetStatus(center));

        // Each of the disc's 5 rows gains a section ahead and loses one behind:
        var moved = new SectionPos(center.X + 1, center.Y, center.Z);

        harness.Level.MoveTicket(ticket, moved);
        harness.Level.Update();

        var queued = harness.Level.GetQueuedSections().ToHashSet();
        var leftBehind = Box(center, 2, 0).Where(position => IsInsideCylinder(center, position, 2, 0) && !IsInsideCylinder(moved, position, 2, 0)).ToArray();

        // The moved disc is queued without the generating center, and the 5 sections left behind are not:
        Assert.Equal(13 - 1, harness.Level.PendingCount);
        Assert.Equal(1, harness.Level.InFlightCount);
        Assert.Equal(5, leftBehind.Length);
        Assert.All(leftBehind, position => Assert.DoesNotContain(position, queued));
    }

    [Fact]
    public void Ticket_IsSatisfiedOnceARebuildFindsItsLoadShapeLoaded_UntilItMoves()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);
        harness.TickUntilSettled();

        // The rebuild that queued the shape found sections to load:
        Assert.False(ticket.IsSatisfied);

        // Another ticket rebuilds the queue, which finds nothing left to load in the first shape:
        var other = harness.Level.AddTicket(SurfaceSection(10, 10), horizontalRadius: 0, verticalRadius: 0);
        harness.Level.Update();

        Assert.True(ticket.IsSatisfied);
        Assert.False(other.IsSatisfied);

        var moved = new SectionPos(center.X + 1, center.Y, center.Z);

        harness.Level.MoveTicket(ticket, moved);

        Assert.False(ticket.IsSatisfied);

        harness.TickUntilSettled();

        foreach (var position in Box(moved, 1, 0).Where(position => IsInsideCylinder(moved, position, 1, 0)))
        {
            Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(position));
        }
    }

    [Fact]
    public void LoadQueue_HoldsEveryUnloadedWantedSectionOverRandomTicketChangesAndPartialLoads()
    {
        using var harness = new Harness(slotCount: 3);
        var random = new Random(2468);
        var tickets = new List<LevelTicket>();
        var surfaceY = SurfaceSection(0, 0).Y;

        for (var step = 0; step < 300; step++)
        {
            ApplyRandomTicketChange(harness.Level, tickets, random, surfaceY - 2, surfaceY + 2);

            // Generation completes only now and then, so some sections stay queued over many updates and some tickets get satisfied:
            if (random.Next(3) == 0)
            {
                harness.Scheduler.Drain();
            }

            harness.Level.Update();

            AssertLoadQueueHoldsTheUnloadedWantedSections(harness.Level, tickets, step);
        }
    }

    [Fact]
    public void TicketCounts_MatchABruteForceCountOverRandomTicketChanges()
    {
        using var harness = new Harness();
        var random = new Random(1234);
        var tickets = new List<LevelTicket>();

        for (var step = 0; step < 400; step++)
        {
            ApplyRandomTicketChange(harness.Level, tickets, random, -20, 20);
            AssertTicketCountsMatchBruteForce(harness.Level, tickets, step);
        }
    }

    [Fact]
    public void Residency_MatchesABruteForceModelOverRandomTicketChangesAndUpdates()
    {
        using var harness = new Harness(unloadsPerUpdate: 16);
        var random = new Random(4321);
        var tickets = new List<LevelTicket>();
        var readySlots = new Dictionary<SectionPos, int>();
        var meshChanges = new List<SectionMeshChange>();

        // Every load shape stays above the highest surface, where a section loads in the update that wants it, with no job:
        var minimumY = LowestSkySectionY() + 2;
        var maximumY = minimumY + 20;

        for (var step = 0; step < 400; step++)
        {
            var changes = random.Next(1, 3);

            for (var change = 0; change < changes; change++)
            {
                ApplyRandomTicketChange(harness.Level, tickets, random, minimumY, maximumY);
            }

            harness.Level.Update();

            AssertResidencyMatchesModel(harness.Level, tickets, readySlots, step);

            // The sky holds no surface, so nothing is reported, and the update looked at every newly ready section:
            harness.Level.TakeMeshChanges(meshChanges);
            Assert.Empty(meshChanges);
            Assert.Equal(0, harness.Level.NewlyReadyCount);

            // A synchronous load now and then, which the next update unloads unless a ticket claims it first:
            if (random.Next(4) == 0)
            {
                var near = tickets.Count > 0 ? tickets[random.Next(tickets.Count)].Center : new SectionPos(0, minimumY, 0);
                var position = new SectionPos(near.X + random.Next(-5, 6), Math.Max(near.Y + random.Next(-3, 4), minimumY), near.Z + random.Next(-5, 6));

                readySlots[position] = harness.Level.GetOrLoadSection(position).SlotIndex;
            }
        }

        foreach (var ticket in tickets)
        {
            harness.Level.RemoveTicket(ticket);
        }

        harness.Level.Update();

        for (var update = 0; update < 1000 && harness.Level.UnloadQueueCount > 0; update++)
        {
            harness.Level.Update();
        }

        Assert.Equal(0, harness.Level.UnloadQueueCount);
        Assert.Equal(0, harness.Level.SectionEntryCount);
    }

    [Fact]
    public void RemovedTicket_IsRejected()
    {
        using var harness = new Harness();
        using var other = new Harness();

        var ticket = harness.Level.AddTicket(SurfaceSection(0, 0), horizontalRadius: 0, verticalRadius: 0);

        Assert.Throws<ArgumentException>(() => other.Level.MoveTicket(ticket, new SectionPos(0, 0, 0)));

        harness.Level.RemoveTicket(ticket);

        Assert.Throws<ArgumentException>(() => harness.Level.RemoveTicket(ticket));
        Assert.Throws<ArgumentException>(() => harness.Level.MoveTicket(ticket, new SectionPos(0, 0, 0)));
    }

    [Fact]
    public void TicketRemovedWhileGenerating_DiscardsTheResult()
    {
        using var harness = new Harness(slotCount: 1);
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.Level.Update();

        Assert.Equal(SectionStatus.Generating, harness.Level.GetStatus(center));

        harness.Level.RemoveTicket(ticket);

        Assert.Equal(SectionStatus.Generating, harness.Level.GetStatus(center));

        harness.Scheduler.Drain();
        harness.Level.Update();

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
        Assert.Equal(0, harness.Level.InFlightCount);
    }

    [Fact]
    public void GetOrLoadSection_WithoutATicket_UnloadsOnTheNextUpdate()
    {
        using var harness = new Harness();
        var position = SurfaceSection(0, 0);

        var section = harness.Level.GetOrLoadSection(position);

        Assert.Equal(position, section.Position);
        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(position));

        harness.Level.Update();

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
    }

    [Fact]
    public void GetOrLoadSection_ClaimedByATicketBeforeTheNextUpdate_StaysLoaded()
    {
        using var harness = new Harness(slotCount: 1);
        var position = SurfaceSection(0, 0);

        harness.Level.GetOrLoadSection(position);
        harness.Level.AddTicket(position, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(position));
    }

    [Fact]
    public void GetOrLoadSection_WhileGenerating_KeepsTheSynchronousSectionAndDropsTheJob()
    {
        using var harness = new Harness(slotCount: 1);
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.Level.Update();

        Assert.Equal(SectionStatus.Generating, harness.Level.GetStatus(center));

        var loaded = harness.Level.GetOrLoadSection(center);

        harness.Scheduler.Drain();
        harness.Level.Update();

        Assert.Equal(0, harness.Level.InFlightCount);
        Assert.True(harness.Level.TryGetSection(center, out var section));
        Assert.Equal(loaded.SlotIndex, section.SlotIndex);
    }

    [Fact]
    public void GetOrLoadSection_OfALoadedSection_ReturnsIt()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.True(harness.Level.TryGetSection(center, out var section));
        Assert.Equal(section.SlotIndex, harness.Level.GetOrLoadSection(center).SlotIndex);
    }

    [Fact]
    public void GetContentSides_OfGeneratedAndSynchronousLoads_MatchesTheirContent()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);
        var synchronous = new SectionPos(center.X + 3, center.Y, center.Z);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 1);
        harness.TickUntilSettled();
        harness.Level.GetOrLoadSection(synchronous);

        var content = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var material = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);

        foreach (var position in new[] { center with { Y = center.Y - 1 }, center, center with { Y = center.Y + 1 }, synchronous })
        {
            var min = position.GetOrigin();

            Assert.True(harness.Level.ReadRange(min, min + SectionPos.SectionMask, content.Span, material.Span));
            Assert.Equal(SectionContentSidesTests.BruteForce(content.Span), harness.Level.GetContentSides(position));
        }

        // The keep margin is claimed but not loaded:
        Assert.Equal(default(SectionContentSides), harness.Level.GetContentSides(new SectionPos(center.X + 1, center.Y, center.Z)));
    }

    [Fact]
    public void ReadRange_AcrossLoadedSections_MatchesTheGenerator()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 2, verticalRadius: 1);
        harness.TickUntilSettled();

        // The box runs from inside the lower neighbor on every axis to inside the upper one, and no face is on a brick boundary:
        var min = center.GetOrigin() - new Vector3i(5, 3, 7);
        var maxInclusive = center.GetOrigin() + new Vector3i(SectionPos.SectionSize + 6, SectionPos.SectionSize + 2, SectionPos.SectionSize + 9);
        var size = maxInclusive - min + 1;

        var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var expectedContent = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var expectedMaterial = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        Assert.True(harness.Level.ReadRange(min, maxInclusive, content.Span, material.Span));

        new TerrainGraphSource(Graph).Generate(min, maxInclusive, expectedContent.Span, expectedMaterial.Span);

        AssertSameBox(expectedContent.Span, content.Span, min, "content");
        AssertSameBox(expectedMaterial.Span, material.Span, min, "material");
    }

    [Fact]
    public void ReadRange_OverAnUnloadedSection_ReadsAirThereAndReportsIt()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        // The box is the loaded section and the unloaded one after it along X:
        var min = center.GetOrigin();
        var maxInclusive = min + new Vector3i(2 * SectionPos.SectionSize - 1, SectionPos.SectionMask, SectionPos.SectionMask);
        var size = maxInclusive - min + 1;

        var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var loadedContent = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var loadedMaterial = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);

        Assert.False(harness.Level.ReadRange(min, maxInclusive, content.Span, material.Span));

        new TerrainGraphSource(Graph).Generate(min, min + SectionPos.SectionMask, loadedContent.Span, loadedMaterial.Span);

        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    var loaded = x < SectionPos.SectionSize;

                    Assert.Equal(loaded ? loadedContent.Span[x, y, z] : (byte)0, content.Span[x, y, z]);
                    Assert.Equal(loaded ? loadedMaterial.Span[x, y, z] : SurfaceMaterial.Null, material.Span[x, y, z]);
                }
            }
        }
    }

    [Fact]
    public void ReadPoint_AgreesWithReadRangeAtNegativeCoordinates()
    {
        using var harness = new Harness();
        var center = SurfaceSection(-1, -2);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        var min = center.GetOrigin();
        var content = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var material = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);

        Assert.True(harness.Level.ReadRange(min, min + SectionPos.SectionMask, content.Span, material.Span));

        for (var z = 0; z < SectionPos.SectionSize; z += 3)
        {
            for (var y = 0; y < SectionPos.SectionSize; y++)
            {
                for (var x = 0; x < SectionPos.SectionSize; x += 5)
                {
                    Assert.True(harness.Level.ReadPoint(min + new Vector3i(x, y, z), out var pointContent, out var pointMaterial));
                    Assert.Equal(content.Span[x, y, z], pointContent);
                    Assert.Equal(material.Span[x, y, z], pointMaterial);
                }
            }
        }

        Assert.False(harness.Level.ReadPoint(min - Vector3i.One, out var airContent, out var airMaterial));
        Assert.Equal(0, airContent);
        Assert.Equal(SurfaceMaterial.Null, airMaterial);
    }

    [Fact]
    public void ReadRange_RejectsADestinationOfTheWrongSize()
    {
        using var harness = new Harness();

        Assert.Throws<ArgumentException>(() =>
        {
            var content = Memory3D<byte>.Allocate(4, 4, 4);
            var material = Memory3D<byte>.Allocate(4, 4, 5);

            harness.Level.ReadRange(Vector3i.Zero, new Vector3i(3, 3, 3), content.Span, material.Span);
        });
    }

    [Fact]
    public void RunningScheduler_LoadsTheShapeAndDisposeWaitsForWorkInFlight()
    {
        WgAssert.InitGameThread();

        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 2 });
        var level = new WorldLevel(scheduler, Graph);
        var center = SurfaceSection(0, 0);

        scheduler.Start();

        level.AddTicket(center, horizontalRadius: 1, verticalRadius: 0);

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (level.GetStatus(center) != SectionStatus.Ready || level.InFlightCount > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The level did not load in time.");

            level.Update();
            Thread.Sleep(1);
        }

        level.AddTicket(SurfaceSection(4, 4), horizontalRadius: 1, verticalRadius: 0);
        level.Update();

        Assert.True(level.InFlightCount > 0);

        level.Dispose();
        scheduler.Dispose();

        Assert.Equal(0, level.InFlightCount);
    }

    [Fact]
    public void MeshChanges_MatchABruteForceModelOverMovesOnTheSurface()
    {
        WgAssert.InitGameThread();

        // Real generation on the surface, so the work runs on every core:
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
        var level = new WorldLevel(scheduler, Graph, slotCount: 32, unloadsPerUpdate: 8);
        var model = new MeshChangeModel(level);

        scheduler.Start();

        try
        {
            const int horizontalRadius = 3;
            const int verticalRadius = 1;

            var center = SurfaceSection(0, 0);
            var ticket = level.AddTicket(center, horizontalRadius, verticalRadius);

            model.AddRegion(center, horizontalRadius, verticalRadius);
            UpdateUntilSettled(level, model, "Add");

            // Short steps that come back over loaded and unloaded sections, then a jump the shapes cannot overlap across and the jump back:
            var columns = new[] { (1, 0), (2, 0), (2, 1), (1, 1), (0, 0), (-2, -1), (20, 0), (0, 0), (1, -2) };

            foreach (var (columnX, columnZ) in columns)
            {
                center = SurfaceSection(columnX, columnZ);
                level.MoveTicket(ticket, center);
                model.AddRegion(center, horizontalRadius, verticalRadius);
                UpdateUntilSettled(level, model, $"Move to ({columnX}, {columnZ})");

                // A keep margin section is claimed, so a synchronous load of it stays and can complete the neighborhoods beside it.
                // One past the keep shape is unloaded by the next update, before anything looks at it:
                level.GetOrLoadSection(new SectionPos(center.X + horizontalRadius + WorldLevel.KeepMargin, center.Y, center.Z));
                level.GetOrLoadSection(new SectionPos(center.X, center.Y, center.Z - horizontalRadius - WorldLevel.KeepMargin - 2));
                UpdateUntilSettled(level, model, $"Synchronous loads at ({columnX}, {columnZ})");
            }

            level.RemoveTicket(ticket);
            UpdateUntilSettled(level, model, "Remove");

            Assert.Equal(0, model.ReportedCount);
            Assert.True(model.RetireCount > 0, "Nothing was retired.");

            // The jump back reloads sections that were reported before, which have to be reported again:
            Assert.True(model.MeshCount > model.DistinctMeshCount, "No section was reported again after it was loaded again.");        }
        finally
        {
            level.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void ReportMeshAgain_WithTheNeighborhoodReady_ReportsTheSectionOnTheNextUpdate()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);
        var changes = new List<SectionMeshChange>();

        harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);
        harness.TickUntilSettled();
        LoadDiagonalNeighbors(harness.Level, center);
        harness.Level.Update();
        harness.Level.TakeMeshChanges(changes);

        AssertOnlyMesh(center, changes);

        // A section that was never reported is left alone:
        harness.Level.ReportMeshAgain(center with { X = center.X + 1 });
        harness.Level.ReportMeshAgain(center);
        harness.Level.Update();
        harness.Level.TakeMeshChanges(changes);

        AssertOnlyMesh(center, changes);
    }

    [Fact]
    public void ReportMeshAgain_WithANeighborUnloaded_ReportsTheSectionOnceTheNeighborIsBack()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);
        var changes = new List<SectionMeshChange>();

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);
        harness.TickUntilSettled();
        LoadDiagonalNeighbors(harness.Level, center);
        harness.Level.Update();
        harness.Level.TakeMeshChanges(changes);

        AssertOnlyMesh(center, changes);

        // One section along -X takes the +X diagonal neighbors out of the keep shape, and the center stays in the load shape:
        harness.Level.MoveTicket(ticket, center with { X = center.X - 1 });
        harness.TickUntilSettled();

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(new SectionPos(center.X + 1, center.Y, center.Z + 1)));
        Assert.Equal(SectionStatus.Ready, harness.Level.GetStatus(center));

        harness.Level.ReportMeshAgain(center);
        harness.Level.Update();
        harness.Level.TakeMeshChanges(changes);

        Assert.Empty(changes);

        // Back at the center, the diagonal neighbors are claimed by the keep margin again and load synchronously:
        harness.Level.MoveTicket(ticket, center);
        harness.TickUntilSettled();
        LoadDiagonalNeighbors(harness.Level, center);
        harness.Level.Update();
        harness.Level.TakeMeshChanges(changes);

        AssertOnlyMesh(center, changes);
    }

    /// <summary>
    ///     Loads the sections diagonal to <paramref name="center"/> in X and Z on its three layers, which a ticket of radius 1 claims in its keep margin but never loads, so the center's neighborhood is complete.
    /// </summary>
    private static void LoadDiagonalNeighbors(WorldLevel level, SectionPos center)
    {
        for (var y = center.Y - 1; y <= center.Y + 1; y++)
        {
            foreach (var (offsetX, offsetZ) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
            {
                level.GetOrLoadSection(new SectionPos(center.X + offsetX, y, center.Z + offsetZ));
            }
        }
    }

    /// <summary>
    ///     Checks that the changes are one Mesh of <paramref name="position"/>, and clears them.
    /// </summary>
    private static void AssertOnlyMesh(SectionPos position, List<SectionMeshChange> changes)
    {
        var change = Assert.Single(changes);

        Assert.Equal(SectionMeshChangeKind.Mesh, change.Kind);
        Assert.Equal(position, change.Position);

        changes.Clear();
    }

    /// <summary>
    ///     The sections reported for meshing, checked after every update against the level's statuses and a brute-force scan of each section's edges.
    /// </summary>
    private sealed class MeshChangeModel(WorldLevel level)
    {
        private const int OwnedSampleSize = SectionPos.SectionSize + 1;

        private readonly HashSet<SectionPos> _reported = [];
        private readonly HashSet<SectionPos> _everReported = [];
        private readonly HashSet<SectionPos> _region = [];
        private readonly Dictionary<SectionPos, bool> _surfaces = [];
        private readonly List<SectionMeshChange> _changes = [];
        private readonly Memory3D<byte> _content = Memory3D<byte>.Allocate(OwnedSampleSize, OwnedSampleSize, OwnedSampleSize);
        private readonly Memory3D<byte> _material = Memory3D<byte>.Allocate(OwnedSampleSize, OwnedSampleSize, OwnedSampleSize);

        public int MeshCount { get; private set; }
        public int RetireCount { get; private set; }
        public int ReportedCount => _reported.Count;
        public int DistinctMeshCount => _everReported.Count;

        /// <summary>
        ///     Adds the sections a ticket's placement can load or complete to the ones every check scans.
        /// </summary>
        public void AddRegion(SectionPos center, int horizontalRadius, int verticalRadius)
        {
            foreach (var position in Box(center, horizontalRadius + WorldLevel.KeepMargin + 1, verticalRadius + WorldLevel.KeepMargin + 1))
            {
                _region.Add(position);
            }
        }

        public void Check(string context)
        {
            _changes.Clear();
            level.TakeMeshChanges(_changes);

            foreach (var change in _changes)
            {
                var position = change.Position;
                var name = $"{context}: section ({position.X}, {position.Y}, {position.Z})";

                if (change.Kind == SectionMeshChangeKind.Retire)
                {
                    if (!_reported.Remove(position))
                    {
                        Assert.Fail($"{name} was retired without being reported.");
                    }

                    if (level.GetStatus(position) == SectionStatus.Ready)
                    {
                        Assert.Fail($"{name} was retired but is still ready.");
                    }

                    RetireCount++;
                    continue;
                }

                if (!_reported.Add(position))
                {
                    Assert.Fail($"{name} was reported again without being retired.");
                }

                if (!IsNeighborhoodReady(position))
                {
                    Assert.Fail($"{name} was reported before it and its 26 neighbors were ready.");
                }

                if (!HasSurface(position))
                {
                    Assert.Fail($"{name} was reported, but none of the edges its cells own crosses the iso level.");
                }

                _everReported.Add(position);
                MeshCount++;
            }

            foreach (var position in _reported)
            {
                var status = level.GetStatus(position);

                if (status != SectionStatus.Ready)
                {
                    Assert.Fail($"{context}: section ({position.X}, {position.Y}, {position.Z}) is {status}, and its mesh was not retired.");
                }
            }

            foreach (var position in _region)
            {
                if (!_reported.Contains(position) && level.GetStatus(position) == SectionStatus.Ready && IsNeighborhoodReady(position) && HasSurface(position))
                {
                    Assert.Fail($"{context}: section ({position.X}, {position.Y}, {position.Z}) and its neighbors are ready and it holds a surface, but it was not reported.");
                }
            }
        }

        private bool IsNeighborhoodReady(SectionPos position)
        {
            foreach (var neighbor in Box(position, 1, 1))
            {
                if (level.GetStatus(neighbor) != SectionStatus.Ready)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Whether an edge some cell of the section owns crosses the iso level, read from the level, so the section and its neighbors have to be ready.
        ///     The content never changes, so each section is scanned once.
        /// </summary>
        private bool HasSurface(SectionPos position)
        {
            if (_surfaces.TryGetValue(position, out var cached))
            {
                return cached;
            }

            // The edges run from each cell's low corner to the next sample on each axis, so they reach one sample into the positive neighbors:
            var min = position.GetOrigin();

            Assert.True(level.ReadRange(min, min + SectionPos.SectionSize, _content.Span, _material.Span));

            var hasSurface = HasCrossingOwnedEdge(_content.Span);

            _surfaces[position] = hasSurface;

            return hasSurface;
        }

        private static bool HasCrossingOwnedEdge(Span3D<byte> content)
        {
            for (var z = 0; z < SectionPos.SectionSize; z++)
            {
                for (var y = 0; y < SectionPos.SectionSize; y++)
                {
                    for (var x = 0; x < SectionPos.SectionSize; x++)
                    {
                        var solid = IsSolid(content[x, y, z]);

                        if (IsSolid(content[x + 1, y, z]) != solid || IsSolid(content[x, y + 1, z]) != solid || IsSolid(content[x, y, z + 1]) != solid)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static bool IsSolid(byte content)
        {
            return content > WorldConstants.ContentIsoLevel;
        }
    }

    /// <summary>
    ///     Updates a level on a running scheduler, checking the model after every update, until nothing is pending, generating or waiting to unload.
    /// </summary>
    private static void UpdateUntilSettled(WorldLevel level, MeshChangeModel model, string context)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        for (var update = 0;; update++)
        {
            level.Update();
            model.Check($"{context}, update {update}");

            if (level.PendingCount == 0 && level.InFlightCount == 0 && level.UnloadQueueCount == 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"{context}: the level did not settle in time.");
            Thread.Sleep(1);
        }
    }

    /// <summary>
    ///     The section holding the surface in the middle of the given section column.
    /// </summary>
    internal static SectionPos SurfaceSection(int sectionX, int sectionZ)
    {
        var x = SectionPos.SectionToVoxelCoord(sectionX) + SectionPos.SectionSize / 2;
        var z = SectionPos.SectionToVoxelCoord(sectionZ) + SectionPos.SectionSize / 2;
        var surfaceY = (int)MathF.Floor(new TerrainGraphSource(Graph).Elevation(x, z) / WorldConstants.VoxelSize);

        return new SectionPos(sectionX, SectionPos.VoxelToSectionCoord(surfaceY), sectionZ);
    }

    /// <summary>
    ///     The lowest section whose first sample is one sample above the highest surface.
    /// </summary>
    internal static int LowestSkySectionY()
    {
        var sectionHeight = SectionPos.SectionSize * WorldConstants.VoxelSize;

        return (int)MathF.Ceiling((Graph.MaxSurfaceY + WorldConstants.VoxelSize) / sectionHeight);
    }

    internal static IEnumerable<SectionPos> Box(SectionPos center, int horizontalRadius, int verticalRadius)
    {
        for (var z = center.Z - horizontalRadius; z <= center.Z + horizontalRadius; z++)
        {
            for (var y = center.Y - verticalRadius; y <= center.Y + verticalRadius; y++)
            {
                for (var x = center.X - horizontalRadius; x <= center.X + horizontalRadius; x++)
                {
                    yield return new SectionPos(x, y, z);
                }
            }
        }
    }

    /// <summary>
    ///     Counts each section's keep shapes by testing every section of every ticket's box, and checks the level's counts and its entries against that.
    ///     The level is never updated here, so every section is unloaded and has an entry exactly when some keep shape holds it.
    /// </summary>
    private static void AssertTicketCountsMatchBruteForce(WorldLevel level, List<LevelTicket> tickets, int step)
    {
        var expectedCounts = new Dictionary<SectionPos, int>();

        foreach (var ticket in tickets)
        {
            var keepHorizontal = ticket.HorizontalRadius + WorldLevel.KeepMargin;
            var keepVertical = ticket.VerticalRadius + WorldLevel.KeepMargin;

            foreach (var position in Box(ticket.Center, keepHorizontal, keepVertical))
            {
                if (IsInsideCylinder(ticket.Center, position, keepHorizontal, keepVertical))
                {
                    expectedCounts[position] = expectedCounts.GetValueOrDefault(position) + 1;
                }
            }
        }

        Assert.Equal(expectedCounts.Count, level.SectionEntryCount);

        foreach (var (position, expected) in expectedCounts)
        {
            var actual = level.GetTicketCount(position);

            if (actual != expected)
            {
                Assert.Fail($"Step {step}: section ({position.X}, {position.Y}, {position.Z}) has {actual} ticket reference(s), expected {expected}.");
            }
        }
    }

    /// <summary>
    ///     Checks right after an update that every section inside some load shape that is neither generating nor loaded is queued, whichever tickets are satisfied, and that nothing outside every load shape is.
    ///     A queued section may be generating or loaded when two load shapes queued it and one of its entries was taken.
    /// </summary>
    private static void AssertLoadQueueHoldsTheUnloadedWantedSections(WorldLevel level, List<LevelTicket> tickets, int step)
    {
        var wanted = new HashSet<SectionPos>();

        foreach (var ticket in tickets)
        {
            foreach (var position in Box(ticket.Center, ticket.HorizontalRadius, ticket.VerticalRadius))
            {
                if (IsInsideCylinder(ticket.Center, position, ticket.HorizontalRadius, ticket.VerticalRadius))
                {
                    wanted.Add(position);
                }
            }
        }

        var queued = level.GetQueuedSections().ToHashSet();

        foreach (var position in wanted)
        {
            if (level.GetStatus(position) == SectionStatus.None && !queued.Contains(position))
            {
                Assert.Fail($"Step {step}: section ({position.X}, {position.Y}, {position.Z}) is unloaded inside a load shape but not queued.");
            }
        }

        foreach (var position in queued)
        {
            if (!wanted.Contains(position))
            {
                Assert.Fail($"Step {step}: section ({position.X}, {position.Y}, {position.Z}) is queued outside every load shape.");
            }
        }
    }

    /// <summary>
    ///     Adds, removes or moves a ticket at random, keeping every center's Y in [<paramref name="minimumY"/>, <paramref name="maximumY"/>].
    /// </summary>
    private static void ApplyRandomTicketChange(WorldLevel level, List<LevelTicket> tickets, Random random, int minimumY, int maximumY)
    {
        var choice = random.Next(10);

        if (tickets.Count == 0 || (choice == 0 && tickets.Count < 4))
        {
            var start = new SectionPos(random.Next(-20, 21), random.Next(minimumY, maximumY + 1), random.Next(-20, 21));

            tickets.Add(level.AddTicket(start, random.Next(0, 4), random.Next(0, 3)));
        }
        else if (choice == 1)
        {
            var index = random.Next(tickets.Count);

            level.RemoveTicket(tickets[index]);
            tickets.RemoveAt(index);
        }
        else
        {
            var ticket = tickets[random.Next(tickets.Count)];
            var center = ticket.Center;

            // Mostly short moves along every axis, and now and then a jump the two placements cannot overlap across:
            var target = choice == 2
                ? new SectionPos(center.X + random.Next(-40, 41), center.Y + random.Next(-20, 21), center.Z + random.Next(-40, 41))
                : new SectionPos(center.X + random.Next(-3, 4), center.Y + random.Next(-3, 4), center.Z + random.Next(-3, 4));

            level.MoveTicket(ticket, new SectionPos(target.X, Math.Clamp(target.Y, minimumY, maximumY), target.Z));
        }
    }

    /// <summary>
    ///     Checks every section a ticket claims or that was loaded at the previous check against a brute-force model, right after an update.
    ///     With every shape above the surface, an update loads every section a load shape holds, and a loaded section no ticket holds waits in the unload queue.
    ///     A section that stayed loaded since the previous check has to be the same section, never one destroyed and loaded again.
    /// </summary>
    private static void AssertResidencyMatchesModel(WorldLevel level, List<LevelTicket> tickets, Dictionary<SectionPos, int> readySlots, int step)
    {
        var expectedCounts = new Dictionary<SectionPos, int>();
        var wanted = new HashSet<SectionPos>();

        foreach (var ticket in tickets)
        {
            var keepHorizontal = ticket.HorizontalRadius + WorldLevel.KeepMargin;
            var keepVertical = ticket.VerticalRadius + WorldLevel.KeepMargin;

            foreach (var position in Box(ticket.Center, keepHorizontal, keepVertical))
            {
                if (IsInsideCylinder(ticket.Center, position, keepHorizontal, keepVertical))
                {
                    expectedCounts[position] = expectedCounts.GetValueOrDefault(position) + 1;
                }

                if (IsInsideCylinder(ticket.Center, position, ticket.HorizontalRadius, ticket.VerticalRadius))
                {
                    wanted.Add(position);
                }
            }
        }

        // A loaded section is either claimed or was loaded at the previous check, so these positions hold every entry:
        var positions = new HashSet<SectionPos>(expectedCounts.Keys);
        positions.UnionWith(readySlots.Keys);

        var entries = 0;
        var queued = 0;
        var nowReady = new Dictionary<SectionPos, int>();

        foreach (var position in positions)
        {
            var expected = expectedCounts.GetValueOrDefault(position);
            var count = level.GetTicketCount(position);
            var status = level.GetStatus(position);
            var isQueued = level.IsUnloadQueued(position);
            var name = $"Step {step}: section ({position.X}, {position.Y}, {position.Z})";

            if (count != expected)
            {
                Assert.Fail($"{name} has {count} ticket reference(s), expected {expected}.");
            }

            if (wanted.Contains(position) && status != SectionStatus.Ready)
            {
                Assert.Fail($"{name} is inside a load shape but {status}.");
            }

            if (status == SectionStatus.Generating)
            {
                Assert.Fail($"{name} is generating above the surface.");
            }

            if (status == SectionStatus.Ready && expected == 0 && !isQueued)
            {
                Assert.Fail($"{name} is loaded with no ticket but not queued for unloading.");
            }

            if (isQueued && status != SectionStatus.Ready)
            {
                Assert.Fail($"{name} is queued for unloading but {status}.");
            }

            var contentSides = level.GetContentSides(position);
            var expectedSides = status == SectionStatus.Ready ? SectionContentSides.FromOneSide(ContentSide.Air) : default;

            if (contentSides != expectedSides)
            {
                Assert.Fail($"{name} is {status} with content sides {contentSides}, expected {expectedSides}.");
            }

            if (status == SectionStatus.Ready)
            {
                Assert.True(level.TryGetSection(position, out var section));

                if (readySlots.TryGetValue(position, out var slot) && slot != section.SlotIndex)
                {
                    Assert.Fail($"{name} was loaded again instead of staying loaded.");
                }

                nowReady[position] = section.SlotIndex;
            }
            else if (expected > 0 && readySlots.ContainsKey(position))
            {
                Assert.Fail($"{name} was unloaded while a ticket holds it.");
            }

            if (expected > 0 || status != SectionStatus.None)
            {
                entries++;
            }

            if (isQueued)
            {
                queued++;
            }
        }

        Assert.Equal(entries, level.SectionEntryCount);
        Assert.Equal(queued, level.UnloadQueueCount);
        Assert.Equal(0, level.PendingCount);
        Assert.Equal(0, level.InFlightCount);

        readySlots.Clear();

        foreach (var (position, slot) in nowReady)
        {
            readySlots[position] = slot;
        }
    }

    private static bool IsInsideCylinder(SectionPos center, SectionPos position, int horizontalRadius, int verticalRadius)
    {
        var deltaX = position.X - center.X;
        var deltaZ = position.Z - center.Z;

        return deltaX * deltaX + deltaZ * deltaZ <= horizontalRadius * horizontalRadius && Math.Abs(position.Y - center.Y) <= verticalRadius;
    }

    private static Section GenerateReference(SectionPos position)
    {
        WgAssert.InitGameThread();

        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);

        return executor.GenerateNow(position, new SectionArena()).Section;
    }

    /// <summary>
    ///     A brick's type and values as a string, so two sections in different arenas compare by content.
    /// </summary>
    private static string DescribeBrick(Section section, VoxelChannel channel, int brick)
    {
        return section.GetBrickType(channel, brick) == BrickType.Uniform
            ? $"Uniform {section.GetUniformValue(channel, brick)}"
            : $"Dense {Convert.ToHexString(section.GetDense(channel, brick).Span)}";
    }

    private static void AssertSameBox(Span3D<byte> expected, Span3D<byte> actual, Vector3i min, string channel)
    {
        for (var z = 0; z < expected.SizeZ; z++)
        {
            for (var y = 0; y < expected.SizeY; y++)
            {
                for (var x = 0; x < expected.SizeX; x++)
                {
                    if (expected[x, y, z] != actual[x, y, z])
                    {
                        Assert.Fail($"The {channel} at ({min.X + x}, {min.Y + y}, {min.Z + z}) is {actual[x, y, z]}, expected {expected[x, y, z]}.");
                    }
                }
            }
        }
    }

    internal static void AssertUniformAir(Section section)
    {
        Assert.Equal(SectionType.Shared, section.Type);

        for (var brick = 0; brick < Section.Bricks; brick++)
        {
            Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, brick));
            Assert.Equal(0, section.GetUniformValue(VoxelChannel.Content, brick));
            Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Material, brick));
            Assert.Equal(SurfaceMaterial.Null, section.GetUniformValue(VoxelChannel.Material, brick));
        }
    }
}

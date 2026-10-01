using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.World;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class LevelTests
{
    private static readonly TerrainGraph Graph = TerrainGraph.Build(11, new TerrainConfig());

    /// <summary>
    ///     A level over a scheduler that is never started, so a tick runs its work on the test thread.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public readonly JobScheduler Scheduler = new(new JobSchedulerDescription { ThreadCount = 2 });
        public readonly Level Level;

        public Harness(int slotCount = SectionGenerationExecutor.DefaultSlotCount, int unloadsPerUpdate = Level.DefaultUnloadsPerUpdate)
        {
            Level = new Level(Scheduler, Graph, slotCount, unloadsPerUpdate);
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

        Assert.Throws<InvalidOperationException>(() => new Level(scheduler, Graph));
    }

    [Fact]
    public void AddTicket_ClaimsTheKeepShapeWithoutLoadingAnything()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);

        // The keep shape is radius 2 horizontally, which is 13 columns, and 5 sections tall, and only the load shape inside it is pending:
        Assert.Equal(13 * 5, harness.Level.SectionEntryCount);
        Assert.Equal(5 * 3, harness.Level.PendingCount);
        Assert.Equal(0, harness.Level.InFlightCount);
        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
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

        // The keep margin is claimed but never pending, and the 5 loaded sections are no longer pending:
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
    public void LowestSkySection_GeneratesAsUniformAir()
    {
        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);
        var arena = new SectionArena();
        var skyY = LowestSkySectionY();

        for (var x = -2; x <= 2; x++)
        {
            for (var z = -2; z <= 2; z++)
            {
                AssertUniformAir(executor.GenerateNow(new SectionPos(x, skyY, z), arena));
            }
        }
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
        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 100_000, center.Y, center.Z + 100_000));

        // The load shape is radius 1 and one section tall, and nothing in it is loaded yet:
        Assert.Equal(5, harness.Level.PendingCount);

        harness.Level.Update();

        foreach (var position in Box(center, 2, 1))
        {
            Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
        }
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
        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 3, center.Y, center.Z + 3));

        // The load disc of radius 2 holds 13 sections and none of them is loaded, since the overlap is in the keep margin:
        Assert.Equal(13, harness.Level.PendingCount);

        harness.Level.Update();

        Assert.True(harness.Level.TryGetSection(overlap, out var after));
        Assert.Equal(before.DescriptorArray, after.DescriptorArray);
        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
    }

    [Fact]
    public void MoveTicket_ToTheSameSection_ChangesNothing()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 1, verticalRadius: 1);
        var pending = harness.Level.PendingCount;

        harness.Level.MoveTicket(ticket, center);

        Assert.Equal(pending, harness.Level.PendingCount);
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
        Assert.Equal(before.DescriptorArray, after.DescriptorArray);
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
    public void Update_DropsPendingSectionsThatLeftEveryLoadShape()
    {
        using var harness = new Harness(slotCount: 1);
        var center = SurfaceSection(0, 0);

        var ticket = harness.Level.AddTicket(center, horizontalRadius: 2, verticalRadius: 0);

        Assert.Equal(13, harness.Level.PendingCount);

        // Each of the disc's 5 rows gains a section ahead and loses one behind, and the one behind stays pending until the next update:
        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 1, center.Y, center.Z));

        Assert.Equal(13 + 5, harness.Level.PendingCount);

        // The update drops the 5 and publishes one section into the only slot:
        harness.Level.Update();

        Assert.Equal(13 - 1, harness.Level.PendingCount);
        Assert.Equal(1, harness.Level.InFlightCount);
    }

    [Fact]
    public void TicketCounts_MatchABruteForceCountOverRandomTicketChanges()
    {
        using var harness = new Harness();
        var random = new Random(1234);
        var tickets = new List<LevelTicket>();

        for (var step = 0; step < 400; step++)
        {
            var choice = random.Next(10);

            if (tickets.Count == 0 || (choice == 0 && tickets.Count < 4))
            {
                var start = new SectionPos(random.Next(-20, 21), random.Next(-20, 21), random.Next(-20, 21));

                tickets.Add(harness.Level.AddTicket(start, random.Next(0, 4), random.Next(0, 3)));
            }
            else if (choice == 1)
            {
                var index = random.Next(tickets.Count);

                harness.Level.RemoveTicket(tickets[index]);
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

                harness.Level.MoveTicket(ticket, target);
            }

            AssertTicketCountsMatchBruteForce(harness.Level, tickets, step);
        }
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
        Assert.Equal(loaded.DescriptorArray, section.DescriptorArray);
    }

    [Fact]
    public void GetOrLoadSection_OfALoadedSection_ReturnsIt()
    {
        using var harness = new Harness();
        var center = SurfaceSection(0, 0);

        harness.Level.AddTicket(center, horizontalRadius: 0, verticalRadius: 0);
        harness.TickUntilSettled();

        Assert.True(harness.Level.TryGetSection(center, out var section));
        Assert.Equal(section.DescriptorArray, harness.Level.GetOrLoadSection(center).DescriptorArray);
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
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 2 });
        var level = new Level(scheduler, Graph);
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

    /// <summary>
    ///     The section holding the surface in the middle of the given section column.
    /// </summary>
    private static SectionPos SurfaceSection(int sectionX, int sectionZ)
    {
        var x = SectionPos.SectionToVoxelCoord(sectionX) + SectionPos.SectionSize / 2;
        var z = SectionPos.SectionToVoxelCoord(sectionZ) + SectionPos.SectionSize / 2;
        var surfaceY = (int)MathF.Floor(new TerrainGraphSource(Graph).Elevation(x, z) / WorldConstants.VoxelSize);

        return new SectionPos(sectionX, SectionPos.VoxelToSectionCoord(surfaceY), sectionZ);
    }

    /// <summary>
    ///     The lowest section whose first sample is one sample above the highest surface.
    /// </summary>
    private static int LowestSkySectionY()
    {
        var sectionHeight = SectionPos.SectionSize * WorldConstants.VoxelSize;

        return (int)MathF.Ceiling((Graph.MaxSurfaceY + WorldConstants.VoxelSize) / sectionHeight);
    }

    private static IEnumerable<SectionPos> Box(SectionPos center, int horizontalRadius, int verticalRadius)
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
    ///     Counts each section's keep shapes by testing every section of every ticket's box, and checks the level's counts, its entries and its pending set against that.
    ///     The level is never updated here, so every section is unloaded and has an entry exactly when some keep shape holds it.
    /// </summary>
    private static void AssertTicketCountsMatchBruteForce(Level level, List<LevelTicket> tickets, int step)
    {
        var expectedCounts = new Dictionary<SectionPos, int>();
        var wanted = new HashSet<SectionPos>();

        foreach (var ticket in tickets)
        {
            var keepHorizontal = ticket.HorizontalRadius + Level.KeepMargin;
            var keepVertical = ticket.VerticalRadius + Level.KeepMargin;

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

        Assert.Equal(expectedCounts.Count, level.SectionEntryCount);

        foreach (var (position, expected) in expectedCounts)
        {
            var actual = level.GetTicketCount(position);

            if (actual != expected)
            {
                Assert.Fail($"Step {step}: section ({position.X}, {position.Y}, {position.Z}) has {actual} ticket reference(s), expected {expected}.");
            }
        }

        foreach (var position in wanted)
        {
            if (!level.IsPending(position))
            {
                Assert.Fail($"Step {step}: section ({position.X}, {position.Y}, {position.Z}) is inside a load shape but not pending.");
            }
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
        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);

        return executor.GenerateNow(position, new SectionArena());
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

    private static void AssertUniformAir(Section section)
    {
        for (var brick = 0; brick < Section.Bricks; brick++)
        {
            Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, brick));
            Assert.Equal(0, section.GetUniformValue(VoxelChannel.Content, brick));
            Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Material, brick));
            Assert.Equal(SurfaceMaterial.Null, section.GetUniformValue(VoxelChannel.Material, brick));
        }
    }
}

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

        public Harness(int slotCount = SectionGenerationExecutor.DefaultSlotCount)
        {
            Level = new Level(Scheduler, Graph, slotCount);
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

        // The keep shape is radius 2 horizontally, which is 13 columns, and 5 sections tall:
        Assert.Equal(13 * 5, harness.Level.PendingCount);
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

        // The keep shape is radius 2 and 3 sections tall, and the 5 loaded sections are no longer pending:
        Assert.Equal(13 * 3 - 5, harness.Level.PendingCount);
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

        // The box around both shapes holds about 3 * 10^10 sections here:
        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 100_000, center.Y, center.Z + 100_000));

        foreach (var position in Box(center, 2, 1))
        {
            Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
        }

        // The keep shape is radius 2 and 3 sections tall, and nothing in it is loaded yet:
        Assert.Equal(13 * 3, harness.Level.PendingCount);
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

        // Three sections along both horizontal axes is past the point where the shapes are walked one at a time:
        harness.Level.MoveTicket(ticket, new SectionPos(center.X + 3, center.Y, center.Z + 3));

        Assert.True(harness.Level.TryGetSection(overlap, out var after));
        Assert.Equal(before.DescriptorArray, after.DescriptorArray);
        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));

        // The keep disc of radius 3 holds 29 columns in 3 layers, and the overlap is the only loaded section in it:
        Assert.Equal(29 * 3 - 1, harness.Level.PendingCount);
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

        Assert.Equal(0, harness.Level.PendingCount);

        foreach (var position in Box(center, 2, 1))
        {
            Assert.Equal(SectionStatus.None, harness.Level.GetStatus(position));
        }
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

        Assert.Equal(SectionStatus.None, harness.Level.GetStatus(center));
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

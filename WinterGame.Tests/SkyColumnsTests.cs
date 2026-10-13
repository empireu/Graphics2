using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Level;
using WinterGame.World.Level.Residency;
using WinterGame.World.Lighting;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SkyColumnsTests
{
    private const int HorizontalRadius = 2;

    /// <summary>
    ///     A level recording its changes, with a ticket on the surface tall enough to reach the lowest sky section, filled over a running scheduler with the sky columns updated every tick.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public readonly JobScheduler Scheduler;
        public readonly WorldLevel Level;
        public readonly SkyColumns Sky;
        public readonly SectionPos Center = LevelTests.SurfaceSection(0, 0);
        public readonly int VerticalRadius;
        public LevelTicket Ticket;

        public Harness()
        {
            WgAssert.InitGameThread();

            Scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
            Level = new WorldLevel(Scheduler, LevelTests.Graph, slotCount: 32, recordChanges: true);
            Sky = new SkyColumns(Level);
            VerticalRadius = Math.Max(Level.LowestSkySectionY - Center.Y, 2);
            Scheduler.Start();
            Ticket = Level.AddTicket(Center, HorizontalRadius, VerticalRadius);
            Fill();
        }

        public int LowestLoadedSectionY => Center.Y - VerticalRadius;

        public void Fill()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);

            do
            {
                Assert.True(DateTime.UtcNow < deadline, "The level did not fill in time.");
                Thread.Sleep(1);
                Level.Update();
                UpdateSky(Level, Sky);
            }
            while (Level.PendingCount > 0 || Level.InFlightCount > 0);
        }

        public void Dispose()
        {
            Level.Dispose();
            Scheduler.Dispose();
        }
    }

    [Fact]
    public void Blocks_IsFalseExactlyWhenAVoxelColumnIsAllAir()
    {
        Assert.True(SkyBlockers.Blocks(ulong.MaxValue));
        Assert.False(SkyBlockers.Blocks(0));

        var oneColumnOpen = ulong.MaxValue;
        var diagonalOpen = ulong.MaxValue;
        var topLayerOpen = ulong.MaxValue;

        for (var y = 0; y < Section.BrickSize; y++)
        {
            oneColumnOpen &= ~(1UL << Section.GetBrickVoxelIndex(2, y, 1));
            diagonalOpen &= ~(1UL << Section.GetBrickVoxelIndex(y, y, 0));
        }

        for (var z = 0; z < Section.BrickSize; z++)
        {
            for (var x = 0; x < Section.BrickSize; x++)
            {
                topLayerOpen &= ~(1UL << Section.GetBrickVoxelIndex(x, Section.BrickSize - 1, z));
            }
        }

        Assert.False(SkyBlockers.Blocks(oneColumnOpen));
        Assert.True(SkyBlockers.Blocks(oneColumnOpen | (1UL << Section.GetBrickVoxelIndex(2, 3, 1))));
        Assert.True(SkyBlockers.Blocks(diagonalOpen));
        Assert.True(SkyBlockers.Blocks(topLayerOpen));
    }

    [Fact]
    public void Scan_SetsTheBitOfEachBlockingBrickInItsColumn()
    {
        var arena = new SectionArena(descriptorArraysPerPage: 4, voxelArraysPerPage: 64);
        var section = arena.Create(new SectionPos(0, 0, 0));

        for (var brickIndex = 0; brickIndex < Section.Bricks; brickIndex++)
        {
            section.SetUniform(VoxelChannel.Content, brickIndex, 0);
        }

        section.SetUniform(VoxelChannel.Content, Section.GetBrickIndex(2, 5, 7), 255);

        // A dense brick with one open voxel column lets the sky through, and one whose hole steps sideways does not:
        var holed = Section.GetBrickIndex(6, 1, 0);
        var stepped = Section.GetBrickIndex(6, 2, 0);

        section.SetDense(VoxelChannel.Content, holed);
        section.SetDense(VoxelChannel.Content, stepped);
        section.GetDense(VoxelChannel.Content, holed).Span.Fill(200);
        section.GetDense(VoxelChannel.Content, stepped).Span.Fill(200);

        for (var y = 0; y < Section.BrickSize; y++)
        {
            section.GetDense(VoxelChannel.Content, holed).Span[Section.GetBrickVoxelIndex(1, y, 1)] = 0;
            section.GetDense(VoxelChannel.Content, stepped).Span[Section.GetBrickVoxelIndex(y, y, 1)] = 0;
        }

        var blockers = new byte[SkyBlockers.ColumnsPerSection];
        var expected = new byte[SkyBlockers.ColumnsPerSection];

        expected[2 + 7 * Section.BricksPerAxis] = 1 << 5;
        expected[6] = 1 << 2;

        Assert.True(SkyBlockers.Scan(section, blockers));
        Assert.Equal(expected, blockers);

        Assert.False(SkyBlockers.Scan(arena.CreateUniform(new SectionPos(1, 0, 0), 0, SurfaceMaterial.Null), blockers));
        Assert.All(blockers, value => Assert.Equal(0, value));

        Assert.True(SkyBlockers.Scan(arena.CreateUniform(new SectionPos(2, 0, 0), 200, SurfaceMaterial.Rock), blockers));
        Assert.All(blockers, value => Assert.Equal(byte.MaxValue, value));
    }

    [Fact]
    public void Floors_MatchAScanOfTheVoxelsAfterAFill()
    {
        using var harness = new Harness();

        var found = 0;

        for (var sectionZ = harness.Center.Z - 1; sectionZ <= harness.Center.Z + 1; sectionZ++)
        {
            for (var sectionX = harness.Center.X - 1; sectionX <= harness.Center.X + 1; sectionX++)
            {
                for (var z = 0; z < Section.BricksPerAxis; z++)
                {
                    for (var x = 0; x < Section.BricksPerAxis; x++)
                    {
                        var brickX = sectionX * Section.BricksPerAxis + x;
                        var brickZ = sectionZ * Section.BricksPerAxis + z;

                        AssertFloorMatchesScan(harness.Level, harness.Sky, brickX, brickZ, harness.LowestLoadedSectionY);

                        harness.Sky.TryGetFloor(brickX, brickZ, out _, out var isFound);
                        found += isFound ? 1 : 0;
                    }
                }
            }
        }

        Assert.True(found > 0, "No column found its top.");
    }

    [Fact]
    public void Digging_LowersTheFloor_AndSealingAboveIt_RaisesIt()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var sky = harness.Sky;
        var brickX = harness.Center.X * Section.BricksPerAxis + 3;
        var brickZ = harness.Center.Z * Section.BricksPerAxis + 4;

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var floor, out var isFound));
        Assert.True(isFound);

        WriteBricks(level, brickX, brickZ, floor - 3, floor - 1, 0, SurfaceMaterial.Null);
        UpdateSky(level, sky);

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var dugFloor, out _));
        Assert.True(dugFloor <= floor - 3, $"The floor is {dugFloor} after digging below {floor}.");
        AssertFloorMatchesScan(level, sky, brickX, brickZ, harness.LowestLoadedSectionY);

        WriteBricks(level, brickX, brickZ, floor + 2, floor + 2, 255, SurfaceMaterial.Rock);
        UpdateSky(level, sky);

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var sealedFloor, out isFound));
        Assert.True(isFound);
        Assert.Equal(floor + 3, sealedFloor);
        AssertFloorMatchesScan(level, sky, brickX, brickZ, harness.LowestLoadedSectionY);
    }

    [Fact]
    public void AnUnloadedColumn_KeepsItsFloor_AndLoadingItAgainCorrectsALostEdit()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var sky = harness.Sky;
        var brickX = harness.Center.X * Section.BricksPerAxis + 5;
        var brickZ = harness.Center.Z * Section.BricksPerAxis + 2;

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var floor, out _));

        WriteBricks(level, brickX, brickZ, floor - 3, floor - 1, 0, SurfaceMaterial.Null);
        UpdateSky(level, sky);

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var dugFloor, out _));
        Assert.True(dugFloor < floor);

        level.RemoveTicket(harness.Ticket);

        for (var update = 0; level.GetStatus(harness.Center) != SectionStatus.None || level.UnloadQueueCount > 0; update++)
        {
            Assert.True(update < 100, "The level did not unload.");
            level.Update();
            UpdateSky(level, sky);
        }

        // The record keeps what the store last held:
        Assert.True(sky.TryGetFloor(brickX, brickZ, out var keptFloor, out _));
        Assert.Equal(dugFloor, keptFloor);

        // The edit was lost with its section, so the regenerated ground puts the floor back:
        harness.Ticket = level.AddTicket(harness.Center, HorizontalRadius, harness.VerticalRadius);
        harness.Fill();

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var reloadedFloor, out _));
        Assert.Equal(floor, reloadedFloor);
        AssertFloorMatchesScan(level, sky, brickX, brickZ, harness.LowestLoadedSectionY);
    }

    [Fact]
    public void AWalk_ContinuesThroughSectionsThatWereReadyBeforeItReachedThem()
    {
        WgAssert.InitGameThread();

        // A scheduler that never starts, since every load here is synchronous:
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var level = new WorldLevel(scheduler, LevelTests.Graph, recordChanges: true);
        var sky = new SkyColumns(level);
        var surface = LevelTests.SurfaceSection(3, -2);
        var lowest = surface.Y - 1;
        var skyY = level.GetLowestSkySectionY(surface.X, surface.Z);
        var brickX = surface.X * Section.BricksPerAxis + 5;
        var brickZ = surface.Z * Section.BricksPerAxis + 2;

        Assert.True(skyY > surface.Y, "The walk would not wait.");

        // The ground first, so the walk waits at the column's lowest sky section:
        level.GetOrLoadSection(surface);
        level.GetOrLoadSection(surface with { Y = lowest });
        UpdateSky(level, sky);

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var waitingFloor, out var isFound));
        Assert.False(isFound);
        Assert.Equal((skyY + 1) * Section.BricksPerAxis, waitingFloor);

        // The rest from the bottom up, all ready before the walk looks at them:
        for (var y = surface.Y + 1; y <= skyY; y++)
        {
            level.GetOrLoadSection(surface with { Y = y });
        }

        UpdateSky(level, sky);

        for (var z = 0; z < Section.BricksPerAxis; z++)
        {
            for (var x = 0; x < Section.BricksPerAxis; x++)
            {
                AssertFloorMatchesScan(level, sky, surface.X * Section.BricksPerAxis + x, surface.Z * Section.BricksPerAxis + z, lowest);
            }
        }

        level.Dispose();
    }

    [Fact]
    public void AWalk_StartsAtItsOwnColumnsSkySection()
    {
        WgAssert.InitGameThread();

        // The game's shape reaches about 220 m, but its origin lies on a river at the ice level, so the walk there starts far below the highest sky:
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var graph = TerrainGraph.Build(3141, new TerrainConfig());
        var level = new WorldLevel(scheduler, graph, recordChanges: true);
        var sky = new SkyColumns(level);
        var surfaceY = SectionPos.VoxelToSectionCoord((int)MathF.Floor(new TerrainGraphSource(graph).Elevation(16, 16) / WorldConstants.VoxelSize));
        var skyY = level.GetLowestSkySectionY(0, 0);

        Assert.True(skyY + 20 < level.LowestSkySectionY);

        for (var y = surfaceY - 1; y <= skyY; y++)
        {
            level.GetOrLoadSection(new SectionPos(0, y, 0));
        }

        UpdateSky(level, sky);

        Assert.True(sky.IsSkyKnown(new SectionPos(0, surfaceY, 0)));

        for (var z = 0; z < Section.BricksPerAxis; z++)
        {
            for (var x = 0; x < Section.BricksPerAxis; x++)
            {
                AssertFloorMatchesScan(level, sky, x, z, surfaceY - 1);
            }
        }

        level.Dispose();
    }

    /// <summary>
    ///     Hands the level's changes to the sky columns, which is the light engine's first step.
    /// </summary>
    private static void UpdateSky(WorldLevel level, SkyColumns sky)
    {
        var changes = new List<SectionChange>();

        level.TakeChanges(changes);
        sky.ApplyChanges(changes.ToArray());
        sky.FloorMoves.Clear();
    }

    /// <summary>
    ///     Checks a column's floor against a scan of the voxels the level holds, from the top of its section column's lowest sky section down to the bottom of <paramref name="lowestLoadedSectionY"/>.
    /// </summary>
    private static void AssertFloorMatchesScan(WorldLevel level, SkyColumns sky, int brickX, int brickZ, int lowestLoadedSectionY)
    {
        var (expectedFloor, expectedFound) = ScanFloor(level, brickX, brickZ, lowestLoadedSectionY);

        Assert.True(sky.TryGetFloor(brickX, brickZ, out var floor, out var isFound), $"Column ({brickX}, {brickZ}) has no record.");
        Assert.True(
            floor == expectedFloor && isFound == expectedFound,
            $"Column ({brickX}, {brickZ}) has the floor {floor} (found {isFound}), but its voxels give {expectedFloor} (found {expectedFound})."
        );
    }

    /// <summary>
    ///     The floor of a column found from its voxels: just above the highest brick whose every voxel column holds a solid voxel, or the bottom of the scan when there is none.
    /// </summary>
    private static (int Floor, bool Found) ScanFloor(WorldLevel level, int brickX, int brickZ, int lowestLoadedSectionY)
    {
        var topBrick = (level.GetLowestSkySectionY(brickX >> Section.BricksPerAxisLog, brickZ >> Section.BricksPerAxisLog) + 1) * Section.BricksPerAxis;
        var bottomBrick = lowestLoadedSectionY * Section.BricksPerAxis;
        var min = new Vector3i(brickX, bottomBrick, brickZ) * Section.BrickSize;
        var max = new Vector3i(brickX + 1, topBrick, brickZ + 1) * Section.BrickSize - 1;
        var size = max - min + 1;
        var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        Assert.True(level.ReadRange(min, max, content.Span, material.Span), $"Column ({brickX}, {brickZ}) is not loaded from {bottomBrick} to {topBrick}.");

        var span = content.Span;

        for (var brick = topBrick - 1; brick >= bottomBrick; brick--)
        {
            var brickMinY = (brick - bottomBrick) * Section.BrickSize;
            var open = false;

            for (var z = 0; z < Section.BrickSize && !open; z++)
            {
                for (var x = 0; x < Section.BrickSize && !open; x++)
                {
                    open = true;

                    for (var y = 0; y < Section.BrickSize; y++)
                    {
                        open &= span[x, brickMinY + y, z] <= WorldConstants.ContentIsoLevel;
                    }
                }
            }

            if (!open)
            {
                return (brick + 1, true);
            }
        }

        return (bottomBrick, false);
    }

    /// <summary>
    ///     Fills the bricks of one column from <paramref name="lowestBrick"/> to <paramref name="highestBrick"/> with one content and material value.
    /// </summary>
    private static void WriteBricks(WorldLevel level, int brickX, int brickZ, int lowestBrick, int highestBrick, byte content, byte material)
    {
        var min = new Vector3i(brickX, lowestBrick, brickZ) * Section.BrickSize;
        var max = new Vector3i(brickX + 1, highestBrick + 1, brickZ + 1) * Section.BrickSize - 1;
        var size = max - min + 1;
        var contentBox = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var materialBox = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        contentBox.Span.Data.Fill(content);
        materialBox.Span.Data.Fill(material);

        Assert.True(level.WriteRange(min, max, contentBox.Span, materialBox.Span), $"The write of bricks {lowestBrick} to {highestBrick} of column ({brickX}, {brickZ}) was refused.");
    }
}

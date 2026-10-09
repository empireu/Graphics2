using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Level;
using WinterGame.World.Level.Residency;
using WinterGame.World.Lighting;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class LightEngineTests
{
    private const int HorizontalRadius = 2;

    /// <summary>
    ///     A level recording its changes and a light engine over the same box as its ticket, which sits on the surface and reaches the lowest sky section, filled over a running scheduler.
    ///     The light's box can be given a smaller vertical radius than the ticket, and the ticket a smaller one than the lowest sky section needs, centered higher or lower.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public readonly JobScheduler Scheduler;
        public readonly WorldLevel Level;
        public readonly LightEngine Light;
        public readonly int VerticalRadius;
        public readonly LevelTicket Ticket;
        public SectionPos Center = LevelTests.SurfaceSection(0, 0);

        public Harness(int? lightVerticalRadius = null, int? ticketVerticalRadius = null, int centerOffsetY = 0)
        {
            WgAssert.InitGameThread();

            Scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
            Level = new WorldLevel(Scheduler, LevelTests.Graph, slotCount: 32, recordChanges: true);
            Center = Center with { Y = Center.Y + centerOffsetY };
            VerticalRadius = ticketVerticalRadius ?? Math.Max(Level.LowestSkySectionY - Center.Y, 2);
            Light = new LightEngine(Level, HorizontalRadius, lightVerticalRadius ?? VerticalRadius);
            Scheduler.Start();
            Ticket = Level.AddTicket(Center, HorizontalRadius, VerticalRadius);
            Fill();
        }

        public void Fill()
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);

            do
            {
                Assert.True(DateTime.UtcNow < deadline, "The level did not fill in time.");
                Thread.Sleep(1);
                Level.Update();
                Light.Update(Center);
            }
            while (Level.PendingCount > 0 || Level.InFlightCount > 0);
        }

        public void MoveTo(SectionPos center)
        {
            Center = center;
            Level.MoveTicket(Ticket, center);
            Fill();
        }

        public void Dispose()
        {
            Level.Dispose();
            Scheduler.Dispose();
        }
    }

    [Fact]
    public void Flood_SpreadsOnlyThroughVoxelsThatShareAFace()
    {
        var corner = 1UL << Section.GetBrickVoxelIndex(0, 0, 0);
        var diagonal = 1UL << Section.GetBrickVoxelIndex(1, 1, 0);

        Assert.Equal(corner, CubeMask.Flood(corner | diagonal, corner));
        Assert.False(BrickAir.IsOneComponent(corner | diagonal));

        // A wall one voxel thick at x 2 leaves two halves:
        var wall = 0UL;

        for (var z = 0; z < Section.BrickSize; z++)
        {
            for (var y = 0; y < Section.BrickSize; y++)
            {
                wall |= 1UL << Section.GetBrickVoxelIndex(2, y, z);
            }
        }

        var air = ~wall;
        var low = CubeMask.Flood(air, corner);

        Assert.False(BrickAir.IsOneComponent(air));
        Assert.Equal(CubeMask.LowX | (CubeMask.LowX << 1), low);
        Assert.Equal(CubeMask.HighX, CubeMask.Flood(air, CubeMask.HighX));

        Assert.True(BrickAir.IsOneComponent(BrickAir.Full));
        Assert.True(BrickAir.IsOneComponent(0));
        Assert.Equal(0, BrickAir.GetOpenColumnVoxel(BrickAir.Full));
        Assert.Equal(-1, BrickAir.GetOpenColumnVoxel(0));

        var column = 0UL;

        for (var y = 0; y < Section.BrickSize; y++)
        {
            column |= 1UL << Section.GetBrickVoxelIndex(1, y, 2);
        }

        Assert.Equal(Section.GetBrickVoxelIndex(1, 0, 2), BrickAir.GetOpenColumnVoxel(column));
        Assert.Equal(-1, BrickAir.GetOpenColumnVoxel(column & ~(1UL << Section.GetBrickVoxelIndex(1, 2, 2))));
    }

    [Fact]
    public void Light_IsSettledAfterAFill()
    {
        using var harness = new Harness();

        AssertSettled(harness.Level, harness.Light, "After the fill");
    }

    [Fact]
    public void Light_IsSettledAfterTheBoxMoves()
    {
        using var harness = new Harness();

        var start = harness.Center;

        harness.MoveTo(start with { X = start.X + 1 });
        AssertSettled(harness.Level, harness.Light, "After a step along X");

        harness.MoveTo(new SectionPos(start.X + 1, start.Y - 1, start.Z - 1));
        AssertSettled(harness.Level, harness.Light, "After a step down and along Z");

        harness.MoveTo(start with { X = start.X + 40 });
        AssertSettled(harness.Level, harness.Light, "After a jump");
    }

    /// <summary>
    ///     A sky shaft in the box's last section column on the low X side lights a tunnel running into the next column, and a step of the box along X leaves the shaft behind.
    /// </summary>
    [Fact]
    public void LightFromASectionTheBoxLeft_GoesWithIt()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var light = harness.Light;
        var start = harness.Center;
        var shaftBrickX = (start.X - HorizontalRadius + 1) * Section.BricksPerAxis - 1;
        var brickZ = start.Z * Section.BricksPerAxis;
        var lowestFloor = int.MaxValue;

        for (var x = shaftBrickX; x <= shaftBrickX + 3; x++)
        {
            Assert.True(light.TryGetSkyFloor(x, brickZ, out var floor, out _));
            lowestFloor = Math.Min(lowestFloor, floor);
        }

        var tunnelY = lowestFloor - 6;

        Assert.True(tunnelY > (start.Y - harness.VerticalRadius) * Section.BricksPerAxis, "The tunnel would lie below the loaded sections.");

        var skyTop = (level.LowestSkySectionY + 1) * SectionPos.SectionSize - 1;
        var lowZ = brickZ * Section.BrickSize;
        var highZ = lowZ + Section.BrickSize - 1;
        var tunnelMinY = tunnelY * Section.BrickSize;

        WriteBox(level, new Vector3i(shaftBrickX * Section.BrickSize, tunnelMinY, lowZ), new Vector3i((shaftBrickX + 1) * Section.BrickSize - 1, skyTop, highZ), 0, SurfaceMaterial.Null);
        WriteBox(level, new Vector3i((shaftBrickX + 1) * Section.BrickSize, tunnelMinY, lowZ), new Vector3i((shaftBrickX + 4) * Section.BrickSize - 1, tunnelMinY + Section.BrickSize - 1, highZ), 0, SurfaceMaterial.Null);
        light.Update(start);

        Assert.Equal(LightEngine.MaxLevel - 1, GetLight(light, shaftBrickX + 1, tunnelY, brickZ));
        Assert.Equal(LightEngine.MaxLevel - 3, GetLight(light, shaftBrickX + 3, tunnelY, brickZ));

        harness.MoveTo(start with { X = start.X + 1 });

        for (var x = shaftBrickX + 1; x <= shaftBrickX + 3; x++)
        {
            Assert.Equal(0, GetLight(light, x, tunnelY, brickZ));
        }

        AssertSettled(level, light, "After the box left the shaft");
    }

    /// <summary>
    ///     A box one section high over a deeper ticket, and a shaft dug down past the box's bottom and then deeper, which moves a floor without touching the box.
    /// </summary>
    [Fact]
    public void AFloorMovingBelowTheBox_LeavesItSettled()
    {
        using var harness = new Harness(lightVerticalRadius: 0);

        var level = harness.Level;
        var light = harness.Light;
        var brickX = harness.Center.X * Section.BricksPerAxis + 3;
        var brickZ = harness.Center.Z * Section.BricksPerAxis + 3;
        var boxBottom = harness.Center.Y * Section.BricksPerAxis;
        var skyTop = (level.LowestSkySectionY + 1) * SectionPos.SectionSize - 1;
        var lowX = brickX * Section.BrickSize;
        var lowZ = brickZ * Section.BrickSize;
        var high = new Vector3i(lowX + Section.BrickSize - 1, skyTop, lowZ + Section.BrickSize - 1);

        WriteBox(level, new Vector3i(lowX, (boxBottom - 2) * Section.BrickSize, lowZ), high, 0, SurfaceMaterial.Null);
        light.Update(harness.Center);
        WriteBox(level, new Vector3i(lowX, (boxBottom - 4) * Section.BrickSize, lowZ), high, 0, SurfaceMaterial.Null);
        light.Update(harness.Center);

        Assert.True(light.TryGetSkyFloor(brickX, brickZ, out var floor, out _));
        Assert.True(floor <= boxBottom - 4, $"The floor {floor} did not follow the shaft below the box at {boxBottom}.");
        AssertSettled(level, light, "After the floor moved below the box");
    }

    /// <summary>
    ///     A sealed cave in the top bricks of a section, then a shaft down to that section's top, written into the sections above it only.
    ///     The cave's section is not written, so only the floor moving into it tells the box to light it as sky.
    /// </summary>
    [Fact]
    public void AShaftDugIntoACave_LightsTheCaveAsSky()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var light = harness.Light;
        var brickX = harness.Center.X * Section.BricksPerAxis + 3;
        var brickZ = harness.Center.Z * Section.BricksPerAxis + 3;

        Assert.True(light.TryGetSkyFloor(brickX, brickZ, out var floor, out _));

        var caveTop = ((floor - 2) >> Section.BricksPerAxisLog) << Section.BricksPerAxisLog;
        var caveBottom = caveTop - 2;

        Assert.True(caveBottom > (harness.Center.Y - harness.VerticalRadius) * Section.BricksPerAxis, "The cave would lie below the loaded sections.");

        var lowX = brickX * Section.BrickSize;
        var lowZ = brickZ * Section.BrickSize;
        var highX = (brickX + 2) * Section.BrickSize - 1;
        var highZ = (brickZ + 2) * Section.BrickSize - 1;
        var skyTop = (level.LowestSkySectionY + 1) * SectionPos.SectionSize - 1;

        WriteBox(level, new Vector3i(lowX, caveBottom * Section.BrickSize, lowZ), new Vector3i(highX, caveTop * Section.BrickSize - 1, highZ), 0, SurfaceMaterial.Null);
        light.Update(harness.Center);

        Assert.Equal(0, GetLight(light, brickX + 1, caveTop - 1, brickZ + 1));

        WriteBox(level, new Vector3i(lowX, caveTop * Section.BrickSize, lowZ), new Vector3i(lowX + Section.BrickSize - 1, skyTop, lowZ + Section.BrickSize - 1), 0, SurfaceMaterial.Null);
        light.Update(harness.Center);

        Assert.Equal(LightEngine.MaxLevel, GetLight(light, brickX, caveTop - 1, brickZ));
        Assert.Equal(LightEngine.MaxLevel - 1, GetLight(light, brickX + 1, caveTop - 1, brickZ));
        AssertSettled(level, light, "With the shaft open into the cave");
    }

    /// <summary>
    ///     A ticket that stops short of the lowest sky section leaves every walk waiting above it, so the box holds none of its ready sections.
    ///     A column ticket up to the lowest sky section lets one column's walk pass, and the box takes that column.
    /// </summary>
    [Fact]
    public void ASectionBelowAWaitingWalk_StaysOutOfTheBox_UntilTheWalkPasses()
    {
        using var harness = new Harness(ticketVerticalRadius: 1, centerOffsetY: -1);

        var level = harness.Level;
        var light = harness.Light;
        var window = light.Window;
        var center = harness.Center;

        Assert.True(center.Y + 1 < level.LowestSkySectionY, "The ticket reaches the lowest sky section.");
        Assert.Equal(SectionStatus.Ready, level.GetStatus(center));

        for (var slot = 0; slot < window.SlotCount; slot++)
        {
            Assert.False(window.IsPresent(slot), $"The box holds section ({window.GetSection(slot).X}, {window.GetSection(slot).Y}, {window.GetSection(slot).Z}) below a waiting walk.");
        }

        level.AddTicket(center with { Y = level.LowestSkySectionY }, 0, level.LowestSkySectionY - center.Y);
        harness.Fill();

        Assert.True(window.IsPresent(window.GetSlot(center)), "The box did not take the column the walk passed.");
        Assert.False(window.IsPresent(window.GetSlot(center with { X = center.X + 1 })), "The box took a column whose walk waits.");
        AssertSettled(level, light, "With one column's walk past the box");
    }

    /// <summary>
    ///     A shaft dug down to the top of a section that is not loaded leaves its walk waiting there, so the box lets go of the loaded section below it.
    ///     Loading the missing section finds the floor where the walk waited without moving it, and the box takes the section below back.
    /// </summary>
    [Fact]
    public void AFloorFoundWhereItsWalkWaited_GivesTheBoxTheSectionBelow()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var light = harness.Light;
        var window = light.Window;
        var center = harness.Center;
        var lowest = center with { Y = center.Y - harness.VerticalRadius };
        var missing = lowest with { Y = lowest.Y + 1 };
        var missingTop = (missing.Y + 1) * Section.BricksPerAxis;
        var brickX = center.X * Section.BricksPerAxis + 3;
        var brickZ = center.Z * Section.BricksPerAxis + 3;

        // The ticket moves up off the two lowest layers, and then a ticket of one section loads the lowest one again under the shaft:
        level.MoveTicket(harness.Ticket, center with { Y = center.Y + 3 });
        harness.Fill();
        level.AddTicket(lowest, 0, 0);
        harness.Fill();

        Assert.Equal(SectionStatus.Ready, level.GetStatus(lowest));
        Assert.NotEqual(SectionStatus.Ready, level.GetStatus(missing));
        Assert.True(light.TryGetSkyFloor(brickX, brickZ, out var floor, out _) && floor > missingTop, $"The floor {floor} is not above the shaft's bottom {missingTop}.");
        Assert.True(window.IsPresent(window.GetSlot(lowest)), "The box does not hold the lowest section.");

        var lowX = brickX * Section.BrickSize;
        var lowZ = brickZ * Section.BrickSize;
        var skyTop = (level.LowestSkySectionY + 1) * SectionPos.SectionSize - 1;

        WriteBox(level, new Vector3i(lowX, missingTop * Section.BrickSize, lowZ), new Vector3i(lowX + Section.BrickSize - 1, skyTop, lowZ + Section.BrickSize - 1), 0, SurfaceMaterial.Null);
        light.Update(center);

        Assert.True(light.TryGetSkyFloor(brickX, brickZ, out floor, out var isFound));
        Assert.Equal(missingTop, floor);
        Assert.False(isFound);
        Assert.False(window.IsPresent(window.GetSlot(lowest)), "The box kept a section below a waiting walk.");

        level.AddTicket(missing, 0, 0);
        harness.Fill();

        Assert.True(light.TryGetSkyFloor(brickX, brickZ, out floor, out isFound));
        Assert.True(isFound && floor == missingTop, $"The missing section's top brick under the shaft does not block: floor {floor}, found {isFound}.");
        Assert.True(window.IsPresent(window.GetSlot(lowest)), "The box did not take back the section below the found floor.");
        AssertSettled(level, light, "With the floor found where its walk waited");
    }

    /// <summary>
    ///     A sky shaft and a sealed chamber side by side, with a wall one voxel thick between them inside one column of bricks, which then holds air from both.
    ///     Mirrored, the chamber's air in the wall bricks comes first in voxel order, which is the side a rule picking the lowest voxel would light.
    ///     Covered, the wall bricks have a roof, so their light comes in from the shaft beside them instead of down from the sky.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AThinWallInsideABrick_KeepsTheChamberBehindItDark(bool mirrored, bool covered)
    {
        using var harness = new Harness();

        var level = harness.Level;
        var light = harness.Light;
        var wallBrickX = harness.Center.X * Section.BricksPerAxis + 3;
        var firstZ = harness.Center.Z * Section.BricksPerAxis + 3;
        var lowestFloor = int.MaxValue;

        for (var x = wallBrickX - 2; x <= wallBrickX + 2; x++)
        {
            for (var z = firstZ; z < firstZ + 2; z++)
            {
                Assert.True(light.TryGetSkyFloor(x, z, out var floor, out _));
                lowestFloor = Math.Min(lowestFloor, floor);
            }
        }

        // The chamber crosses a section boundary, so a wall brick takes its lit side from the wall brick above it in the next section up:
        var boundary = ((lowestFloor - 6) >> Section.BricksPerAxisLog) << Section.BricksPerAxisLog;
        var chamberBottom = boundary - 2;
        var chamberTop = boundary + 1;
        var shaftBottom = chamberBottom - 1;

        Assert.True(shaftBottom > (harness.Center.Y - harness.VerticalRadius) * Section.BricksPerAxis, "The shaft would reach below the loaded sections.");

        // The shaft and the chamber each take two whole bricks and part of the wall brick, on either side of a wall voxel:
        var wallBrickMinX = wallBrickX * Section.BrickSize;
        var wallX = mirrored ? wallBrickMinX + 1 : wallBrickMinX + 2;
        var lowSideMinX = (wallBrickX - 2) * Section.BrickSize;
        var highSideMaxX = (wallBrickX + 3) * Section.BrickSize - 1;
        var (shaftMinX, shaftMaxX) = mirrored ? (wallX + 1, highSideMaxX) : (lowSideMinX, wallX - 1);
        var (chamberMinX, chamberMaxX) = mirrored ? (lowSideMinX, wallX - 1) : (wallX + 1, highSideMaxX);
        var shaftSide = mirrored ? 1 : -1;
        var lowZ = firstZ * Section.BrickSize;
        var highZ = (firstZ + 2) * Section.BrickSize - 1;
        var skyTop = (level.LowestSkySectionY + 1) * SectionPos.SectionSize - 1;

        WriteBox(level, new Vector3i(shaftMinX, shaftBottom * Section.BrickSize, lowZ), new Vector3i(shaftMaxX, skyTop, highZ), 0, SurfaceMaterial.Null);
        WriteBox(level, new Vector3i(chamberMinX, chamberBottom * Section.BrickSize, lowZ), new Vector3i(chamberMaxX, (chamberTop + 1) * Section.BrickSize - 1, highZ), 0, SurfaceMaterial.Null);

        if (covered)
        {
            WriteBox(level, new Vector3i(wallBrickMinX, (chamberTop + 1) * Section.BrickSize, lowZ), new Vector3i(wallBrickMinX + Section.BrickSize - 1, (chamberTop + 3) * Section.BrickSize - 1, highZ), 255, SurfaceMaterial.Rock);
        }

        light.Update(harness.Center);

        var wallLevel = covered ? LightEngine.MaxLevel - 1 : LightEngine.MaxLevel;

        Assert.Equal(LightEngine.MaxLevel, GetLight(light, wallBrickX + shaftSide, chamberBottom + 1, firstZ));
        Assert.Equal(wallLevel, GetLight(light, wallBrickX, chamberBottom + 1, firstZ));
        Assert.Equal(0, GetChamberMaximum(light, wallBrickX - shaftSide, -shaftSide, firstZ, chamberBottom, chamberTop));
        AssertSettled(level, light, "With the wall closed");

        // A hole through the wall of one brick lets the light in:
        var holeY = (chamberBottom + 1) * Section.BrickSize;
        var holeMin = new Vector3i(wallX, holeY, lowZ);
        var holeMax = new Vector3i(wallX, holeY + Section.BrickSize - 1, lowZ + Section.BrickSize - 1);

        WriteBox(level, holeMin, holeMax, 0, SurfaceMaterial.Null);
        light.Update(harness.Center);

        Assert.Equal(wallLevel - 1, GetLight(light, wallBrickX - shaftSide, chamberBottom + 1, firstZ));
        AssertSettled(level, light, "With a hole in the wall");

        // Closing it takes the light back out:
        WriteBox(level, holeMin, holeMax, 255, SurfaceMaterial.Rock);
        light.Update(harness.Center);

        Assert.Equal(0, GetChamberMaximum(light, wallBrickX - shaftSide, -shaftSide, firstZ, chamberBottom, chamberTop));
        AssertSettled(level, light, "With the hole closed again");
    }

    private static int GetLight(LightEngine light, int brickX, int brickY, int brickZ)
    {
        Assert.True(light.TryGetLight(brickX, brickY, brickZ, out var level), $"Brick ({brickX}, {brickY}, {brickZ}) is outside the light box.");

        return level;
    }

    /// <summary>
    ///     The brightest brick of the two chamber bricks that lie wholly behind the wall, starting at <paramref name="firstX"/> and stepping away from the wall by <paramref name="step"/>.
    /// </summary>
    private static int GetChamberMaximum(LightEngine light, int firstX, int step, int firstZ, int chamberBottom, int chamberTop)
    {
        var maximum = 0;

        for (var x = firstX; x != firstX + 2 * step; x += step)
        {
            for (var y = chamberBottom; y <= chamberTop; y++)
            {
                for (var z = firstZ; z < firstZ + 2; z++)
                {
                    maximum = Math.Max(maximum, GetLight(light, x, y, z));
                }
            }
        }

        return maximum;
    }

    private static void WriteBox(WorldLevel level, Vector3i min, Vector3i max, byte content, byte material)
    {
        var size = max - min + 1;
        var contentBox = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var materialBox = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        contentBox.Span.Data.Fill(content);
        materialBox.Span.Data.Fill(material);

        Assert.True(level.WriteRange(min, max, contentBox.Span, materialBox.Span), $"The write from ({min.X}, {min.Y}, {min.Z}) to ({max.X}, {max.Y}, {max.Z}) was refused.");
    }

    /// <summary>
    ///     Checks that the box holds exactly the ready sections inside it whose sky is known, and that its light has settled:
    ///     a cell is at the full level exactly when it has air and stands at or above its floor, a dimmer lit cell has a brighter neighbor it can have come from,
    ///     and every neighbor the lit air of a cell touches across a face is at most one level darker.
    /// </summary>
    private static void AssertSettled(WorldLevel level, LightEngine light, string context)
    {
        var window = light.Window;
        var min = window.Min;

        for (var z = 0; z < window.SizeZ; z++)
        {
            for (var y = 0; y < window.SizeY; y++)
            {
                for (var x = 0; x < window.SizeX; x++)
                {
                    var position = new SectionPos(min.X + x, min.Y + y, min.Z + z);
                    var slot = window.GetSlot(position);
                    var isReady = level.GetStatus(position) == SectionStatus.Ready;
                    var isKnown = light.Sky.IsSkyKnown(position);

                    Assert.True((isReady && isKnown) == window.IsPresent(slot), $"{context}: section ({position.X}, {position.Y}, {position.Z}) is ready {isReady} with its sky known {isKnown} but present {window.IsPresent(slot)}.");

                    if (isReady && isKnown)
                    {
                        Assert.Equal(position, window.GetSection(slot));
                    }
                }
            }
        }

        var originX = min.X * Section.BricksPerAxis;
        var originY = min.Y * Section.BricksPerAxis;
        var originZ = min.Z * Section.BricksPerAxis;
        var lit = 0;

        for (var z = 0; z < window.BricksZ; z++)
        {
            for (var y = 0; y < window.BricksY; y++)
            {
                for (var x = 0; x < window.BricksX; x++)
                {
                    var cell = window.GetCell(x, y, z);
                    var word = window.Light[cell];
                    var cellLevel = word & 0xF;
                    var air = window.Air[cell];
                    var where = $"{context}: brick ({originX + x}, {originY + y}, {originZ + z}) at level {cellLevel}";

                    if (air == 0)
                    {
                        Assert.True(cellLevel == 0, $"{where} has no air.");
                        continue;
                    }

                    Assert.True(light.TryGetSkyFloor(originX + x, originZ + z, out var floor, out _), $"{where} has no sky record.");
                    Assert.True(originY + y >= floor == (cellLevel == LightEngine.MaxLevel), $"{where} is wrong for its floor {floor}.");

                    if (cellLevel == 0)
                    {
                        continue;
                    }

                    lit++;

                    var litAir = LightWord.GetLitAir(word, air);
                    var hasBrighterNeighbor = false;

                    foreach (var (offsetX, offsetY, offsetZ, footprint) in GetFootprints(litAir))
                    {
                        var neighborX = x + offsetX;
                        var neighborY = y + offsetY;
                        var neighborZ = z + offsetZ;

                        if ((uint)neighborX >= (uint)window.BricksX || (uint)neighborY >= (uint)window.BricksY || (uint)neighborZ >= (uint)window.BricksZ)
                        {
                            continue;
                        }

                        var neighbor = window.GetCell(neighborX, neighborY, neighborZ);
                        var neighborLevel = window.Light[neighbor] & 0xF;

                        hasBrighterNeighbor |= neighborLevel > cellLevel;

                        if (cellLevel >= 2 && (footprint & window.Air[neighbor]) != 0)
                        {
                            Assert.True(neighborLevel >= cellLevel - 1, $"{where} touches the air of brick ({originX + neighborX}, {originY + neighborY}, {originZ + neighborZ}) at level {neighborLevel}.");
                        }
                    }

                    Assert.True(cellLevel == LightEngine.MaxLevel || hasBrighterNeighbor, $"{where} has no brighter neighbor to have come from.");
                }
            }
        }

        Assert.True(lit > 0, $"{context}: nothing is lit.");
    }

    /// <summary>
    ///     For each face, the step to the neighbor and the lit air's voxels on that face moved onto the neighbor's facing face.
    /// </summary>
    private static (int X, int Y, int Z, ulong Footprint)[] GetFootprints(ulong litAir)
    {
        return
        [
            (1, 0, 0, (litAir & CubeMask.HighX) >> 3),
            (-1, 0, 0, (litAir & CubeMask.LowX) << 3),
            (0, 1, 0, (litAir & CubeMask.HighY) >> 12),
            (0, -1, 0, (litAir & CubeMask.LowY) << 12),
            (0, 0, 1, (litAir & CubeMask.HighZ) >> 48),
            (0, 0, -1, (litAir & CubeMask.LowZ) << 48)
        ];
    }
}

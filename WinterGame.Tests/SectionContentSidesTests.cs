using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Render;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SectionContentSidesTests
{
    private const int Size = SectionPos.SectionSize;

    /// <summary>
    ///     The samples a section's mesh window reads below its first cell, and the cells it emits start there.
    /// </summary>
    private const int WindowLowMargin = 2;

    /// <summary>
    ///     The window holds the cells, the margin below them and two samples above them.
    /// </summary>
    private const int WindowSize = Size + WindowLowMargin + 2;

    private static readonly TerrainGraph Graph = TerrainGraph.Build(11, new TerrainConfig());

    private static readonly ContentSide[] AllSides = [ContentSide.None, ContentSide.Air, ContentSide.Solid, ContentSide.Mixed];

    [Fact]
    public void Constructor_KeepsEveryFieldInItsOwnBits()
    {
        foreach (var interior in AllSides)
        {
            foreach (var lowX in AllSides)
            {
                foreach (var lowY in AllSides)
                {
                    foreach (var lowZ in AllSides)
                    {
                        var sides = new SectionContentSides(interior, lowX, lowY, lowZ);

                        Assert.Equal(interior, sides.Interior);
                        Assert.Equal(lowX, sides.LowX);
                        Assert.Equal(lowY, sides.LowY);
                        Assert.Equal(lowZ, sides.LowZ);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(0, ContentSide.Air)]
    [InlineData((int)WorldConstants.ContentIsoLevel, ContentSide.Air)]
    [InlineData(WorldConstants.ContentIsoLevel + 1, ContentSide.Solid)]
    [InlineData(255, ContentSide.Solid)]
    public void FromContent_OfOneValue_PutsTheIsoLevelItselfOnTheAirSide(int value, ContentSide expected)
    {
        var content = FillSection(new SectionPos(0, 0, 0), (_, _, _) => (byte)value);

        Assert.Equal(SectionContentSides.FromOneSide(expected), SectionContentSides.FromContent(content.Span));
    }

    [Fact]
    public void FromContent_ReadsEachLowFaceOnItsOwn()
    {
        var origin = new SectionPos(0, 0, 0);

        Assert.Equal(
            new SectionContentSides(ContentSide.Mixed, ContentSide.Mixed, ContentSide.Solid, ContentSide.Mixed),
            SectionContentSides.FromContent(FillSection(origin, (_, y, _) => y < 10 ? byte.MaxValue : (byte)0).Span)
        );

        Assert.Equal(
            new SectionContentSides(ContentSide.Mixed, ContentSide.Solid, ContentSide.Mixed, ContentSide.Mixed),
            SectionContentSides.FromContent(FillSection(origin, (x, _, _) => x == 0 ? byte.MaxValue : (byte)0).Span)
        );

        Assert.Equal(
            new SectionContentSides(ContentSide.Mixed, ContentSide.Mixed, ContentSide.Mixed, ContentSide.Solid),
            SectionContentSides.FromContent(FillSection(origin, (_, _, z) => z == 0 ? byte.MaxValue : (byte)0).Span)
        );

        // One solid sample at the far corner touches no low face:
        Assert.Equal(
            new SectionContentSides(ContentSide.Mixed, ContentSide.Air, ContentSide.Air, ContentSide.Air),
            SectionContentSides.FromContent(FillSection(origin, (x, y, z) => x == Size - 1 && y == Size - 1 && z == Size - 1 ? byte.MaxValue : (byte)0).Span)
        );
    }

    [Fact]
    public void FromContent_MatchesABruteForceScanOverRandomPlanes()
    {
        var random = new Random(1234);

        for (var plane = 0; plane < 300; plane++)
        {
            var field = RandomPlane(random);
            var content = FillSection(new SectionPos(0, 0, 0), field);

            Assert.Equal(BruteForce(content.Span), SectionContentSides.FromContent(content.Span));
        }
    }

    [Fact]
    public void FromSection_MatchesABruteForceScanOverRandomPlanesStoredInBricks()
    {
        var random = new Random(4321);
        var arena = new SectionArena();

        for (var plane = 0; plane < 300; plane++)
        {
            var position = new SectionPos(plane, 0, 0);
            var content = FillSection(new SectionPos(0, 0, 0), RandomPlane(random));

            // Every other section stores its bricks of one value dense, which the generator never does but the scan must still read:
            var section = StoreSection(arena, position, content.Span, allDense: plane % 2 == 1);

            Assert.Equal(BruteForce(content.Span), SectionContentSides.FromSection(section));

            arena.Destroy(position);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData((int)WorldConstants.ContentIsoLevel, false)]
    [InlineData((int)WorldConstants.ContentIsoLevel, true)]
    [InlineData(WorldConstants.ContentIsoLevel + 1, false)]
    [InlineData(WorldConstants.ContentIsoLevel + 1, true)]
    [InlineData(255, true)]
    public void FromSection_OfOneValue_PutsTheIsoLevelItselfOnTheAirSide(int value, bool allDense)
    {
        var content = FillSection(new SectionPos(0, 0, 0), (_, _, _) => (byte)value);
        var section = StoreSection(new SectionArena(), new SectionPos(0, 0, 0), content.Span, allDense);

        Assert.Equal(SectionContentSides.FromContent(content.Span), SectionContentSides.FromSection(section));
    }

    [Fact]
    public void FromSection_ReadsEachLowFaceOnItsOwn()
    {
        var arena = new SectionArena();
        var fields = new Func<int, int, int, byte>[]
        {
            (_, y, _) => y < 10 ? byte.MaxValue : (byte)0,
            (x, _, _) => x == 0 ? byte.MaxValue : (byte)0,
            (_, _, z) => z == 0 ? byte.MaxValue : (byte)0,
            (x, y, z) => x == Size - 1 && y == Size - 1 && z == Size - 1 ? byte.MaxValue : (byte)0,
            (x, y, z) => x == 0 && y == 0 && z == 0 ? byte.MaxValue : (byte)0
        };

        for (var index = 0; index < fields.Length; index++)
        {
            var position = new SectionPos(index, 0, 0);
            var content = FillSection(new SectionPos(0, 0, 0), fields[index]);
            var section = StoreSection(arena, position, content.Span, allDense: false);

            Assert.Equal(BruteForce(content.Span), SectionContentSides.FromSection(section));
        }
    }

    [Fact]
    public void HasSurface_IsFalseOnlyWhenTheSectionAndTheFacesTowardItAreOnOneSide()
    {
        var air = SectionContentSides.FromOneSide(ContentSide.Air);
        var solid = SectionContentSides.FromOneSide(ContentSide.Solid);

        Assert.False(SectionContentSides.HasSurface(air, air, air, air));
        Assert.False(SectionContentSides.HasSurface(solid, solid, solid, solid));
        Assert.True(SectionContentSides.HasSurface(new SectionContentSides(ContentSide.Mixed, ContentSide.Air, ContentSide.Air, ContentSide.Air), air, air, air));

        // An all solid section under an all air one owns the edges between them:
        Assert.True(SectionContentSides.HasSurface(solid, solid, air, solid));

        // Only the face toward the section counts, so neighbours that are mixed elsewhere add nothing:
        Assert.False(
            SectionContentSides.HasSurface(
                solid,
                new SectionContentSides(ContentSide.Mixed, ContentSide.Solid, ContentSide.Mixed, ContentSide.Mixed),
                new SectionContentSides(ContentSide.Mixed, ContentSide.Mixed, ContentSide.Solid, ContentSide.Mixed),
                new SectionContentSides(ContentSide.Mixed, ContentSide.Mixed, ContentSide.Mixed, ContentSide.Solid)
            )
        );
    }

    [Fact]
    public void HasSurface_AgreesWithTheMesherOnSurfacesBetweenSections()
    {
        AssertAgreesWithTheMesher((_, y, _) => y < Size ? byte.MaxValue : (byte)0, Line(new SectionPos(0, -1, 0), 0, 1, 0));
        AssertAgreesWithTheMesher((x, _, _) => x < Size ? byte.MaxValue : (byte)0, Line(new SectionPos(-1, 0, 0), 1, 0, 0));
        AssertAgreesWithTheMesher((_, _, z) => z < 0 ? byte.MaxValue : (byte)0, Line(new SectionPos(0, 0, -2), 0, 0, 1));

        // An air face over a solid section, which is the case a flag per section alone would miss:
        AssertAgreesWithTheMesher((_, y, _) => y < 0 ? byte.MaxValue : (byte)0, [new SectionPos(0, -1, 0)], expectSurface: true);
    }

    [Fact]
    public void HasSurface_AgreesWithTheMesherOnRandomPlanes()
    {
        var random = new Random(4321);

        for (var plane = 0; plane < 12; plane++)
        {
            AssertAgreesWithTheMesher(RandomPlane(random), Line(new SectionPos(-1, -1, -1), 1, 1, 1));
        }
    }

    [Fact]
    public void HasSurface_AgreesWithTheMesherAroundTheGeneratedSurface()
    {
        WgAssert.InitGameThread();

        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);
        var arena = new SectionArena();
        var source = new TerrainGraphSource(Graph);
        var generated = new Dictionary<SectionPos, SectionContentSides>();
        var center = SurfaceSection(source);
        var surfaces = 0;
        var empty = 0;

        for (var z = 0; z < 2; z++)
        {
            for (var y = center.Y - 1; y <= center.Y + 1; y++)
            {
                for (var x = 0; x < 2; x++)
                {
                    var position = new SectionPos(x, y, z);
                    var predicted = SectionContentSides.HasSurface(
                        GetGeneratedSides(position),
                        GetGeneratedSides(new SectionPos(x + 1, y, z)),
                        GetGeneratedSides(new SectionPos(x, y + 1, z)),
                        GetGeneratedSides(new SectionPos(x, y, z + 1))
                    );

                    var window = new Window();
                    var windowMin = position.GetOrigin() - WindowLowMargin;

                    source.Generate(windowMin, windowMin + (WindowSize - 1), window.Content.Span, window.Material.Span);

                    Assert.True(predicted == HasQuads(window), $"Section ({x}, {y}, {z}) was predicted {(predicted ? "to have" : "to have no")} surface.");

                    if (predicted)
                    {
                        surfaces++;
                    }
                    else
                    {
                        empty++;
                    }
                }
            }
        }

        Assert.True(surfaces > 0 && empty > 0, $"The sections around the surface gave {surfaces} with surface and {empty} without, and both cases have to be checked.");

        SectionContentSides GetGeneratedSides(SectionPos position)
        {
            if (!generated.TryGetValue(position, out var sides))
            {
                sides = executor.GenerateNow(position, arena).ContentSides;
                generated[position] = sides;
            }

            return sides;
        }
    }

    [Fact]
    public void GenerateNow_ReportsTheContentSidesOfWhatItGenerated()
    {
        WgAssert.InitGameThread();

        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);
        var source = new TerrainGraphSource(Graph);
        var surface = SurfaceSection(source);
        var skyY = SectionPos.VoxelToSectionCoord((int)MathF.Ceiling(Graph.MaxSurfaceY / WorldConstants.VoxelSize)) + 1;
        var content = Memory3D<byte>.Allocate(Size, Size, Size);
        var material = Memory3D<byte>.Allocate(Size, Size, Size);

        foreach (var position in new[] { surface, surface with { Y = surface.Y - 1 }, surface with { Y = surface.Y + 1 }, new SectionPos(0, skyY, 0), new SectionPos(0, -100, 0) })
        {
            var min = position.GetOrigin();

            source.Generate(min, min + SectionPos.SectionMask, content.Span, material.Span);

            Assert.Equal(BruteForce(content.Span), executor.GenerateNow(position, new SectionArena()).ContentSides);
        }
    }

    /// <summary>
    ///     Classifies every sample one at a time.
    /// </summary>
    internal static SectionContentSides BruteForce(Span3D<byte> content)
    {
        var interior = ContentSide.None;
        var lowX = ContentSide.None;
        var lowY = ContentSide.None;
        var lowZ = ContentSide.None;

        for (var z = 0; z < content.SizeZ; z++)
        {
            for (var y = 0; y < content.SizeY; y++)
            {
                for (var x = 0; x < content.SizeX; x++)
                {
                    var side = content[x, y, z] > WorldConstants.ContentIsoLevel ? ContentSide.Solid : ContentSide.Air;

                    interior |= side;

                    if (x == 0)
                    {
                        lowX |= side;
                    }

                    if (y == 0)
                    {
                        lowY |= side;
                    }

                    if (z == 0)
                    {
                        lowZ |= side;
                    }
                }
            }
        }

        return new SectionContentSides(interior, lowX, lowY, lowZ);
    }

    /// <summary>
    ///     Checks every section's prediction against meshing its window, and when <paramref name="expectSurface"/> is given, that the prediction is that.
    /// </summary>
    private static void AssertAgreesWithTheMesher(Func<int, int, int, byte> field, IEnumerable<SectionPos> sections, bool? expectSurface = null)
    {
        foreach (var position in sections)
        {
            var predicted = SectionContentSides.HasSurface(
                Sides(field, position),
                Sides(field, new SectionPos(position.X + 1, position.Y, position.Z)),
                Sides(field, new SectionPos(position.X, position.Y + 1, position.Z)),
                Sides(field, new SectionPos(position.X, position.Y, position.Z + 1))
            );

            var window = new Window();
            var windowMin = position.GetOrigin() - WindowLowMargin;
            var contentSpan = window.Content.Span;
            var materialSpan = window.Material.Span;

            for (var z = 0; z < WindowSize; z++)
            {
                for (var y = 0; y < WindowSize; y++)
                {
                    for (var x = 0; x < WindowSize; x++)
                    {
                        var value = field(windowMin.X + x, windowMin.Y + y, windowMin.Z + z);

                        contentSpan[x, y, z] = value;
                        materialSpan[x, y, z] = value > WorldConstants.ContentIsoLevel ? (byte)0 : SurfaceMaterial.Null;
                    }
                }
            }

            var name = $"Section ({position.X}, {position.Y}, {position.Z})";

            Assert.True(predicted == HasQuads(window), $"{name} was predicted {(predicted ? "to have" : "to have no")} surface.");

            if (expectSurface != null)
            {
                Assert.True(predicted == expectSurface, $"{name} was predicted {(predicted ? "to have" : "to have no")} surface.");
            }
        }
    }

    /// <summary>
    ///     Stores a section's content in bricks the way generation does, a brick of one value uniform, unless <paramref name="allDense"/> stores every brick dense.
    /// </summary>
    private static Section StoreSection(SectionArena arena, SectionPos position, Span3D<byte> content, bool allDense)
    {
        var section = arena.Create(position);
        Span<byte> brick = stackalloc byte[Section.VoxelsPerBrick];

        for (var brickZ = 0; brickZ < Section.BricksPerAxis; brickZ++)
        {
            for (var brickY = 0; brickY < Section.BricksPerAxis; brickY++)
            {
                for (var brickX = 0; brickX < Section.BricksPerAxis; brickX++)
                {
                    for (var z = 0; z < Section.BrickSize; z++)
                    {
                        for (var y = 0; y < Section.BrickSize; y++)
                        {
                            for (var x = 0; x < Section.BrickSize; x++)
                            {
                                brick[Section.GetBrickVoxelIndex(x, y, z)] = content[brickX * Section.BrickSize + x, brickY * Section.BrickSize + y, brickZ * Section.BrickSize + z];
                            }
                        }
                    }

                    var brickIndex = Section.GetBrickIndex(brickX, brickY, brickZ);

                    if (!allDense && !brick.ContainsAnyExcept(brick[0]))
                    {
                        section.SetUniform(VoxelChannel.Content, brickIndex, brick[0]);
                    }
                    else
                    {
                        section.SetDense(VoxelChannel.Content, brickIndex);
                        brick.CopyTo(section.GetDense(VoxelChannel.Content, brickIndex).Span);
                    }

                    section.SetUniform(VoxelChannel.Material, brickIndex, SurfaceMaterial.Null);
                }
            }
        }

        return section;
    }

    private static SectionContentSides Sides(Func<int, int, int, byte> field, SectionPos position)
    {
        return SectionContentSides.FromContent(FillSection(position, field).Span);
    }

    private static Memory3D<byte> FillSection(SectionPos position, Func<int, int, int, byte> field)
    {
        var content = Memory3D<byte>.Allocate(Size, Size, Size);
        var span = content.Span;
        var origin = position.GetOrigin();

        for (var z = 0; z < Size; z++)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    span[x, y, z] = field(origin.X + x, origin.Y + y, origin.Z + z);
                }
            }
        }

        return content;
    }

    /// <summary>
    ///     A content ramp across a plane through or near the section at the origin, often along one axis so a plane can lie on a face.
    /// </summary>
    private static Func<int, int, int, byte> RandomPlane(Random random)
    {
        int normalX;
        int normalY;
        int normalZ;

        do
        {
            normalX = random.Next(-2, 3);
            normalY = random.Next(-2, 3);
            normalZ = random.Next(-2, 3);
        }
        while (normalX == 0 && normalY == 0 && normalZ == 0);

        var reach = (Math.Abs(normalX) + Math.Abs(normalY) + Math.Abs(normalZ)) * Size;
        var offset = random.Next(-reach - 4, reach + 4);
        var scale = random.Next(1, 64);

        return (x, y, z) => (byte)Math.Clamp(WorldConstants.ContentIsoLevel + (normalX * x + normalY * y + normalZ * z - offset) * scale, 0, 255);
    }

    private static IEnumerable<SectionPos> Line(SectionPos first, int stepX, int stepY, int stepZ)
    {
        for (var step = 0; step < 4; step++)
        {
            yield return new SectionPos(first.X + step * stepX, first.Y + step * stepY, first.Z + step * stepZ);
        }
    }

    /// <summary>
    ///     Meshes the section a window was read around with the fairing off, which does not change which quads exist.
    /// </summary>
    private static bool HasQuads(Window window)
    {
        var cellCount = new Vector3i(Size);
        var indices = new List<int>();

        MultiLabelSurfaceNets.Mesh(
            new MultiLabelSurfaceNetsStateStorage(cellCount, 0),
            new MultiLabelSurfaceNetsDescription
            {
                Content = window.Content,
                Material = window.Material,
                IsoLevel = WorldConstants.ContentIsoLevel,
                CellMin = new Vector3i(WindowLowMargin),
                CellCount = cellCount,
                Vertices = [],
                Indices = indices
            }
        );

        return indices.Count > 0;
    }

    private static SectionPos SurfaceSection(TerrainGraphSource source)
    {
        var surfaceY = (int)MathF.Floor(source.Elevation(Size / 2, Size / 2) / WorldConstants.VoxelSize);

        return new SectionPos(0, SectionPos.VoxelToSectionCoord(surfaceY), 0);
    }

    private sealed class Window
    {
        public readonly Memory3D<byte> Content = Memory3D<byte>.Allocate(WindowSize, WindowSize, WindowSize);
        public readonly Memory3D<byte> Material = Memory3D<byte>.Allocate(WindowSize, WindowSize, WindowSize);
    }
}

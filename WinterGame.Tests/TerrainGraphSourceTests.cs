using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;

namespace WinterGame.Tests;

public class TerrainGraphSourceTests
{
    private static TerrainGraphSource CreateSource(int seed = 11)
    {
        return new TerrainGraphSource(seed, new TerrainConfig());
    }

    [Fact]
    public void Generate_MatchesPointReadsAndMarksOnlySolidSamples()
    {
        AssertGenerateMatchesPointReads(CreateSource(), new Vector3i(-6, -4, 2), new Vector3i(9, 8, 14));
    }

    [Fact]
    public void Generate_WritesABoxAboveTheSurfaceAsAirLikePointReads()
    {
        var source = CreateSource();
        var top = (int)MathF.Ceiling(source.Graph.MaxSurfaceY / WorldConstants.VoxelSize);

        // The box starts two metres above the highest surface the graph can make, so every corner of its cells is out of the ground and no sample is evaluated:
        AssertGenerateMatchesPointReads(source, new Vector3i(-16, top + 8, -16), new Vector3i(15, top + 39, 15));
    }

    [Fact]
    public void Elevation_IsInsideTheGeneratedSurface()
    {
        var source = CreateSource();
        var spacing = DensitySampler.SampleSpacing(0);

        for (var x = -8; x <= 8; x += 4)
        {
            for (var z = -8; z <= 8; z += 4)
            {
                var height = source.Elevation(x, z);
                var surface = (int)MathF.Floor(height / spacing);

                // The density field is cell interpolated, so the crossing can move by up to a couple of samples:
                Assert.True(source.SampleContent(new Vector3i(x, surface - 2, z)) > WorldConstants.ContentIsoLevel);
                Assert.True(source.SampleContent(new Vector3i(x, surface + 2, z)) <= WorldConstants.ContentIsoLevel);
            }
        }
    }

    [Fact]
    public void Generate_RejectsABoxThatDoesNotMatchTheDestinations()
    {
        var source = CreateSource();

        Assert.Throws<ArgumentException>(
            () =>
            {
                var content = Memory3D<byte>.Allocate(2, 2, 2);
                var material = Memory3D<byte>.Allocate(2, 2, 2);

                source.Generate(Vector3i.Zero, new Vector3i(0, 0, 0), content.Span, material.Span);
            }
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
            {
                var content = Memory3D<byte>.Allocate(1, 1, 1);
                var material = Memory3D<byte>.Allocate(1, 1, 1);

                source.Generate(Vector3i.Zero, new Vector3i(-1, 0, 0), content.Span, material.Span);
            }
        );
    }

    private static void AssertGenerateMatchesPointReads(TerrainGraphSource source, Vector3i min, Vector3i max)
    {
        var countX = max.X - min.X + 1;
        var countY = max.Y - min.Y + 1;
        var countZ = max.Z - min.Z + 1;

        var content = Memory3D<byte>.Allocate(countX, countY, countZ);
        var material = Memory3D<byte>.Allocate(countX, countY, countZ);

        source.Generate(min, max, content.Span, material.Span);
        var contentSpan = content.Span;
        var materialSpan = material.Span;

        for (var z = 0; z < countZ; z++)
        {
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    var position = new Vector3i(min.X + x, min.Y + y, min.Z + z);

                    Assert.Equal(source.SampleContent(position), contentSpan[x, y, z]);
                    Assert.Equal(
                        contentSpan[x, y, z] > WorldConstants.ContentIsoLevel,
                        materialSpan[x, y, z] != SurfaceMaterial.Null
                    );
                }
            }
        }
    }
}

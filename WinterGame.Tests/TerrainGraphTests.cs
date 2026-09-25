using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation;
using Xunit.Abstractions;

namespace WinterGame.Tests;

public class TerrainGraphTests(ITestOutputHelper output)
{
    private static readonly TerrainConfig Config = new();

    [Fact]
    public void Cells_KeepTheSurfaceWithinAFewBytes()
    {
        const int seed = 7;

        var directGraph = TerrainGraph.Build(seed, Config, interpolate: false);
        var celledGraph = TerrainGraph.Build(seed, Config, interpolate: true);

        var direct = new DensitySampler(directGraph.Graph);
        var celled = new DensitySampler(celledGraph.Graph);

        var min = new Vector3i(-4, 0, -4);
        var max = new Vector3i(31, 0, 31);

        CoverTheSurface((x, z) => direct.Sample(new Vector3i(x, 0, z), 0, directGraph.ElevationRoot), ref min, ref max);

        var samples = 0;
        var maxDelta = 0;
        var surfaceMaxDelta = 0;
        var totalDelta = 0L;

        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                for (var x = min.X; x <= max.X; x++)
                {
                    var position = new Vector3i(x, y, z);
                    var expected = DensitySampler.ContentFromDensity(direct.Sample(position, 0, directGraph.DensityRoot), 0);
                    var actual = DensitySampler.ContentFromDensity(celled.Sample(position, 0, celledGraph.DensityRoot), 0);
                    var delta = Math.Abs(expected - actual);

                    samples++;
                    maxDelta = Math.Max(maxDelta, delta);
                    totalDelta += delta;

                    if (Math.Abs(expected - WorldConstants.ContentIsoLevel) <= 32)
                    {
                        surfaceMaxDelta = Math.Max(surfaceMaxDelta, delta);
                    }
                }
            }
        }

        output.WriteLine($"samples={samples} maxDelta={maxDelta} surfaceMaxDelta={surfaceMaxDelta} meanDelta={(double)totalDelta / samples:F4}");

        Assert.True(maxDelta <= 24, $"The cells move the content by {maxDelta} units, which is more than the 24 the cell size should cost.");
    }

    [Fact]
    public void Material_IsAlwaysOneOfTheProvincesIds()
    {
        var config = new TerrainConfig();
        var graph = TerrainGraph.Build(7, config);
        var sampler = new DensitySampler(graph.Graph);
        var spacing = DensitySampler.SampleSpacing(0);

        var allowed = new HashSet<byte> { SurfaceMaterial.Ice, SurfaceMaterial.Snow };

        foreach (var table in config.Provinces.Tables)
        {
            allowed.Add(table.BelowId);

            for (var band = 0; band < table.BandCount; band++)
            {
                allowed.Add(table.BandId(band));
            }
        }

        for (var z = -8; z <= 8; z += 4)
        {
            for (var x = -8; x <= 8; x += 4)
            {
                var surface = (int)MathF.Floor(sampler.Sample(new Vector3i(x, 0, z), 0, graph.ElevationRoot) / spacing);

                for (var y = surface - 16; y <= surface + 4; y++)
                {
                    var material = (byte)sampler.Sample(new Vector3i(x, y, z), 0, graph.MaterialRoot);

                    Assert.Contains(material, allowed);
                }
            }
        }
    }

    [Fact]
    public void MaxSurfaceY_IsAboveEverySample()
    {
        var graph = TerrainGraph.Build(3, new TerrainConfig());
        var sampler = new DensitySampler(graph.Graph);

        for (var z = -64; z <= 64; z += 8)
        {
            for (var x = -64; x <= 64; x += 8)
            {
                Assert.True(sampler.Sample(new Vector3i(x, 0, z), 0, graph.ElevationRoot) <= graph.MaxSurfaceY);
            }
        }
    }

    /// <summary>
    ///     Grows the box's y range to hold every column's surface, so the comparison is not all saturation.
    /// </summary>
    private static void CoverTheSurface(Func<int, int, float> elevationAt, ref Vector3i min, ref Vector3i max)
    {
        min = new Vector3i(min.X, int.MaxValue, min.Z);
        max = new Vector3i(max.X, int.MinValue, max.Z);

        for (var x = min.X; x <= max.X; x++)
        {
            for (var z = min.Z; z <= max.Z; z++)
            {
                var surface = (int)MathF.Floor(elevationAt(x, z) / WorldConstants.VoxelSize);

                min.Y = Math.Min(min.Y, surface - 2);
                max.Y = Math.Max(max.Y, surface + 2);
            }
        }
    }

    [Fact]
    public void Elevation_IsWhereTheDensityCrossesZero()
    {
        const int seed = 7;

        var graph = TerrainGraph.Build(seed, Config, interpolate: false);
        var sampler = new DensitySampler(graph.Graph);
        var spacing = DensitySampler.SampleSpacing(0);

        for (var x = -32; x <= 32; x += 8)
        {
            for (var z = -32; z <= 32; z += 8)
            {
                var height = sampler.Sample(new Vector3i(x, 0, z), 0, graph.ElevationRoot);
                var surface = (int)MathF.Floor(height / spacing);

                Assert.InRange(sampler.Sample(new Vector3i(x, surface, z), 0, graph.DensityRoot), 0.0f, spacing);
                Assert.InRange(sampler.Sample(new Vector3i(x, surface + 1, z), 0, graph.DensityRoot), -spacing, 0.0f);
            }
        }
    }
}

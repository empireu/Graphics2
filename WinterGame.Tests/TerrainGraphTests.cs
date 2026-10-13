using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;
using Xunit.Abstractions;

namespace WinterGame.Tests;

public class TerrainGraphTests(ITestOutputHelper output)
{
    private static readonly TerrainConfig Config = new();

    /// <summary>
    ///     The game's seed, which has a river through the origin.
    /// </summary>
    private const int IceSeed = 3141;

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

    [Fact]
    public void Ice_TopIsFlatAtTheIceLevel()
    {
        foreach (var interpolate in new[] { false, true })
        {
            var graph = TerrainGraph.Build(IceSeed, Config, interpolate);
            var sampler = new DensitySampler(graph.Graph);
            var level = (int)MathF.Round(Config.IceLevel / WorldConstants.VoxelSize);

            foreach (var (x, z) in FindIceColumns(graph, sampler, depth: 1.0f))
            {
                // Every corner column of the cell lies under the ice, so the blend is exact there too:
                Assert.Equal(Config.IceLevel, sampler.Sample(new Vector3i(x, 0, z), 0, graph.ElevationRoot));
                Assert.Equal(254, DensitySampler.ContentFromDensity(sampler.Sample(new Vector3i(x, level - 1, z), 0, graph.DensityRoot), 0));
                Assert.Equal(WorldConstants.ContentIsoLevel, DensitySampler.ContentFromDensity(sampler.Sample(new Vector3i(x, level, z), 0, graph.DensityRoot), 0));
                Assert.Equal(0, DensitySampler.ContentFromDensity(sampler.Sample(new Vector3i(x, level + 1, z), 0, graph.DensityRoot), 0));
            }
        }
    }

    [Fact]
    public void Ice_FillsOnlyWhereTheGroundLeavesAirBelowTheIceLevel()
    {
        var graph = TerrainGraph.Build(IceSeed, Config, interpolate: false);
        var sampler = new DensitySampler(graph.Graph);
        var spacing = DensitySampler.SampleSpacing(0);
        var roots = new float[graph.Graph.RootCount];
        var iceSamples = 0;
        var bedSamples = 0;

        foreach (var (x, z) in FindIceColumns(graph, sampler, depth: 0.5f))
        {
            var ground = sampler.Sample(new Vector3i(x, 0, z), 0, graph.GroundRoot);

            for (var y = (int)MathF.Floor(ground / spacing) - 8; y <= (int)MathF.Ceiling(Config.IceLevel / spacing) + 4; y++)
            {
                sampler.Sample(new Vector3i(x, y, z), 0, roots);

                var height = y * spacing;
                var material = (byte)roots[graph.MaterialRoot];
                var solid = roots[graph.DensityRoot] > 0.0f;

                Assert.Equal(height < Config.IceLevel, solid);

                if (height > ground && height < Config.IceLevel)
                {
                    Assert.True(material is SurfaceMaterial.Ice or SurfaceMaterial.Snow, $"The fill at ({x}, {y}, {z}) is material {material}.");
                    iceSamples++;
                }
                else if (height < ground && Config.IceLevel - height > Config.SnowThickness + Config.IceThickness)
                {
                    // The skin is measured from the top of the ice, so a bed deeper than it is the bands' stone:
                    Assert.True(material is not (SurfaceMaterial.Ice or SurfaceMaterial.Snow), $"The bed at ({x}, {y}, {z}) is material {material}.");
                    bedSamples++;
                }
            }
        }

        Assert.True(iceSamples > 100 && bedSamples > 100, $"Only {iceSamples} ice and {bedSamples} bed sample(s) were checked.");

        // Above the ice level the solid is the ground's alone:
        for (var z = -64; z <= 64; z += 16)
        {
            for (var x = -64; x <= 64; x += 16)
            {
                var ground = sampler.Sample(new Vector3i(x, 0, z), 0, graph.GroundRoot);
                var top = (int)MathF.Ceiling(Math.Max(ground, Config.IceLevel) / spacing) + 4;

                for (var y = (int)MathF.Ceiling(Config.IceLevel / spacing); y <= top; y++)
                {
                    Assert.Equal(y * spacing < ground, sampler.Sample(new Vector3i(x, y, z), 0, graph.DensityRoot) > 0.0f);
                }
            }
        }
    }

    [Fact]
    public void SurfaceBound_IsAtOrAboveTheTopOfEveryColumn()
    {
        var graph = TerrainGraph.Build(IceSeed, Config);
        var sampler = new DensitySampler(graph.Graph);
        var bound = new DensitySampler(graph.SurfaceBoundGraph);

        for (var z = -2048; z <= 2048; z += 52)
        {
            for (var x = -2048; x <= 2048; x += 52)
            {
                var position = new Vector3i(x, 0, z);
                var top = sampler.Sample(position, 0, graph.ElevationRoot);

                Assert.True(bound.Sample(position, 0) >= top, $"The bound at ({x}, {z}) is below the top {top}.");
                Assert.True(top <= graph.MaxSurfaceY);
            }
        }
    }

    /// <summary>
    ///     Columns near the origin whose ground and the ground of every corner column around them lie at least <paramref name="depth"/> below the ice level.
    /// </summary>
    private static List<(int X, int Z)> FindIceColumns(TerrainGraph graph, DensitySampler sampler, float depth)
    {
        var columns = new List<(int X, int Z)>();
        var cellX = graph.Graph.CellX;
        var cellZ = graph.Graph.CellZ;

        for (var z = -256; z <= 256 && columns.Count < 40; z += 3)
        {
            for (var x = -256; x <= 256 && columns.Count < 40; x += 3)
            {
                var deep = true;
                var cornerX = MathX.FloorDiv(x, cellX) * cellX;
                var cornerZ = MathX.FloorDiv(z, cellZ) * cellZ;

                foreach (var (sampleX, sampleZ) in new[] { (x, z), (cornerX, cornerZ), (cornerX + cellX, cornerZ), (cornerX, cornerZ + cellZ), (cornerX + cellX, cornerZ + cellZ) })
                {
                    deep &= sampler.Sample(new Vector3i(sampleX, 0, sampleZ), 0, graph.GroundRoot) < Config.IceLevel - depth;
                }

                if (deep)
                {
                    columns.Add((x, z));
                }
            }
        }

        Assert.True(columns.Count >= 10, $"Only {columns.Count} column(s) under the ice were found near the origin.");

        return columns;
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

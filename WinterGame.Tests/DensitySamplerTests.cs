using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;

namespace WinterGame.Tests;

public class DensitySamplerTests
{
    [Fact]
    public void Sampler_EvaluatesTheAuthoredArithmetic()
    {
        var builder = new DensityGraphBuilder(1);
        var root = builder.Add(builder.Mul(builder.X, builder.Constant(2.0f)), builder.Constant(1.0f));
        var sampler = new DensitySampler(builder.Compile(root));

        Assert.Equal(2.0f * (3 * WorldConstants.VoxelSize) + 1.0f, sampler.Sample(new Vector3i(3, 0, 0), 0));
    }

    [Fact]
    public void Sampler_ReturnsTheRootOfAConstantGraph()
    {
        var builder = new DensityGraphBuilder(1);
        var sampler = new DensitySampler(builder.Compile(builder.Constant(-2.5f)));

        Assert.Equal(-2.5f, sampler.Sample(new Vector3i(11, -4, 7), 0));
    }

    [Fact]
    public void Sampler_MatchesPointSamplesOverARange()
    {
        foreach (var interpolate in new[] { false, true })
        {
            var sampler = new DensitySampler(HeightFieldGraph(interpolate));
            var min = new Vector3i(-7, -5, 3);
            var max = new Vector3i(6, 2, 12);

            AssertRangeMatchesPoints(sampler, min, max);
        }
    }

    [Fact]
    public void Sampler_OverlappingRangesAgreeOnSharedSamples()
    {
        var sampler = new DensitySampler(HeightFieldGraph(interpolate: true));

        var firstMin = new Vector3i(-3, -2, 0);
        var firstMax = new Vector3i(9, 6, 8);
        var secondMin = new Vector3i(4, 0, 2);
        var secondMax = new Vector3i(14, 9, 12);

        var first = ReadRange(sampler, firstMin, firstMax);
        var second = ReadRange(sampler, secondMin, secondMax);

        var overlapMin = new Vector3i(
            Math.Max(firstMin.X, secondMin.X),
            Math.Max(firstMin.Y, secondMin.Y),
            Math.Max(firstMin.Z, secondMin.Z)
        );

        var overlapMax = new Vector3i(
            Math.Min(firstMax.X, secondMax.X),
            Math.Min(firstMax.Y, secondMax.Y),
            Math.Min(firstMax.Z, secondMax.Z)
        );

        for (var z = overlapMin.Z; z <= overlapMax.Z; z++)
        {
            for (var y = overlapMin.Y; y <= overlapMax.Y; y++)
            {
                for (var x = overlapMin.X; x <= overlapMax.X; x++)
                {
                    Assert.Equal(
                        first[x - firstMin.X, y - firstMin.Y, z - firstMin.Z],
                        second[x - secondMin.X, y - secondMin.Y, z - secondMin.Z]
                    );
                }
            }
        }
    }

    [Fact]
    public void Sampler_InterpolationIsTheTrilinearBlendOfTheCorners()
    {
        var direct = new DensitySampler(HeightFieldGraph(interpolate: false));
        var celled = new DensitySampler(HeightFieldGraph(interpolate: true));

        var graph = celled.Graph;
        var position = new Vector3i(5, 3, -2);

        var cellX = MathX.FloorDiv(position.X, graph.CellX);
        var cellY = MathX.FloorDiv(position.Y, graph.CellY);
        var cellZ = MathX.FloorDiv(position.Z, graph.CellZ);

        var fractionX = (position.X - cellX * graph.CellX) / (float)graph.CellX;
        var fractionY = (position.Y - cellY * graph.CellY) / (float)graph.CellY;
        var fractionZ = (position.Z - cellZ * graph.CellZ) / (float)graph.CellZ;

        var expected = 0.0f;

        for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
        {
            var weightX = (corner & 1) != 0 ? fractionX : 1.0f - fractionX;
            var weightY = (corner & 2) != 0 ? fractionY : 1.0f - fractionY;
            var weightZ = (corner & 4) != 0 ? fractionZ : 1.0f - fractionZ;

            var cornerSample = new Vector3i(
                (cellX + (corner & 1)) * graph.CellX,
                (cellY + ((corner >> 1) & 1)) * graph.CellY,
                (cellZ + ((corner >> 2) & 1)) * graph.CellZ
            );

            expected += direct.Sample(cornerSample, 0) * weightX * weightY * weightZ;
        }

        Assert.Equal(expected, celled.Sample(position, 0));
    }

    [Fact]
    public void Sampler_InterpolationIsExactAtTheCellCorners()
    {
        var direct = new DensitySampler(HeightFieldGraph(interpolate: false));
        var celled = new DensitySampler(HeightFieldGraph(interpolate: true));

        var graph = celled.Graph;

        for (var z = -8; z <= 8; z += graph.CellZ)
        {
            for (var y = -8; y <= 8; y += graph.CellY)
            {
                for (var x = -8; x <= 8; x += graph.CellX)
                {
                    var position = new Vector3i(x, y, z);

                    Assert.Equal(direct.Sample(position, 0), celled.Sample(position, 0));
                }
            }
        }
    }

    [Fact]
    public void Sampler_ReproducesALinearFieldExactly()
    {
        var builder = new DensityGraphBuilder(1);

        // A power-of-two span keeps the gradient's own arithmetic exact, so the identity has to survive the cells bit for bit:
        var gradient = builder.YClampedGradient(builder.Y, -128.0f, 128.0f, -128.0f, 128.0f);
        var sampler = new DensitySampler(builder.Compile(builder.Interpolated(gradient)));

        for (var y = -16; y <= 40; y++)
        {
            Assert.Equal(y * WorldConstants.VoxelSize, sampler.Sample(new Vector3i(0, y, 0), 0));
        }
    }

    [Fact]
    public void Sampler_LodScalesTheSamplePosition()
    {
        var sampler = new DensitySampler(HeightFieldGraph(interpolate: false));

        for (var index = -4; index <= 4; index++)
        {
            Assert.Equal(
                sampler.Sample(new Vector3i(index * 2, index * 2, index * 6), 0),
                sampler.Sample(new Vector3i(index, index, index * 3), 1)
            );
        }
    }

    [Fact]
    public void Sampler_IsDeterministic()
    {
        var graph = HeightFieldGraph(interpolate: true);
        var first = new DensitySampler(graph);
        var second = new DensitySampler(graph);

        for (var index = -8; index <= 8; index++)
        {
            var position = new Vector3i(index, index * 3, -index);

            Assert.Equal(first.Sample(position, 0), second.Sample(position, 0));
        }
    }

    [Fact]
    public void Sampler_InterpolatesABarePositionNode()
    {
        var builder = new DensityGraphBuilder(1);
        var graph = builder.Compile(builder.Interpolated(builder.X));
        var sampler = new DensitySampler(graph);

        Assert.True(graph.HasInterpolation);
        Assert.Empty(graph.CornerProgram);
        Assert.Equal(3 * WorldConstants.VoxelSize, sampler.Sample(new Vector3i(3, 7, -5), 0));
    }

    [Fact]
    public void Sampler_RejectsABoxThatDoesNotMatchTheDestination()
    {
        var sampler = new DensitySampler(HeightFieldGraph(interpolate: true));

        Assert.Throws<ArgumentException>(
            () =>
            {
                var density = Memory3D<float>.Allocate(2, 2, 2);
                sampler.SampleRange(0, new Vector3i(0, 0, 0), new Vector3i(0, 0, 0), density.Span);
            }
        );

        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
            {
                var density = Memory3D<float>.Allocate(1, 1, 1);
                sampler.SampleRange(0, new Vector3i(0, 0, 0), new Vector3i(-1, 0, 0), density.Span);
            }
        );
    }

    [Fact]
    public void SampleSpacing_DoublesPerLevel()
    {
        Assert.Equal(WorldConstants.VoxelSize, DensitySampler.SampleSpacing(0));
        Assert.Equal(WorldConstants.VoxelSize * 2.0f, DensitySampler.SampleSpacing(1));
        Assert.Equal(WorldConstants.VoxelSize * 4.0f, DensitySampler.SampleSpacing(2));
    }

    [Fact]
    public void ContentFromDensity_SaturatesAboutOneSampleOut()
    {
        Assert.Equal(WorldConstants.ContentIsoLevel, DensitySampler.ContentFromDensity(0.0f, 0));
        Assert.Equal(254, DensitySampler.ContentFromDensity(WorldConstants.VoxelSize, 0));
        Assert.Equal(0, DensitySampler.ContentFromDensity(-WorldConstants.VoxelSize, 0));
        Assert.Equal(255, DensitySampler.ContentFromDensity(4.0f * WorldConstants.VoxelSize, 0));

        Assert.Equal(254, DensitySampler.ContentFromDensity(DensitySampler.SampleSpacing(1), 1));
    }

    [Fact]
    public void Sampler_FillsEverySampleSideRootInOnePass()
    {
        var builder = new DensityGraphBuilder(7);
        var hills = builder.Noise2D("hills", frequency: 0.25f);
        var graph = builder.Compile([builder.Add(hills, hills), builder.Mul(hills, builder.Constant(3.0f))]);

        var scratch = new DensityScratch(graph);
        var sampler = new DensitySampler(graph, scratch);

        var min = new Vector3i(-3, -2, 5);
        var max = new Vector3i(4, 3, 9);

        sampler.SampleRange(0, min, max);

        var first = scratch.RangeOutput(0);
        var second = scratch.RangeOutput(1);
        var index = 0;

        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                for (var x = min.X; x <= max.X; x++)
                {
                    var position = new Vector3i(x, y, z);

                    Assert.Equal(sampler.Sample(position, 0, 0), first[index]);
                    Assert.Equal(sampler.Sample(position, 0, 1), second[index]);
                    index++;
                }
            }
        }
    }

    [Fact]
    public void Sampler_SelectIsNeverInterpolated()
    {
        var builder = new DensityGraphBuilder(3);
        var depth = builder.Interpolated(builder.Add(builder.Constant(2.0f), builder.Mul(builder.Y, builder.Constant(-1.0f))));
        var material = builder.Select(builder.Subtract(depth, builder.Constant(0.5f)), builder.Constant(20.0f), builder.Constant(10.0f));
        var sampler = new DensitySampler(builder.Compile([depth, material]));

        var sawShallow = false;
        var sawDeep = false;

        for (var y = -8; y <= 8; y++)
        {
            var value = sampler.Sample(new Vector3i(0, y, 0), 0, 1);

            sawShallow |= value == 20.0f;
            sawDeep |= value == 10.0f;

            Assert.True(value is 20.0f or 10.0f, $"The material root returned {value}, which is neither branch.");
        }

        Assert.True(sawShallow && sawDeep, "The samples did not cross the select.");
    }

    [Fact]
    public void Sampler_ReadsACornerRootAtTheExactPosition()
    {
        var builder = new DensityGraphBuilder(5);
        var shape = builder.Mul(builder.Noise2D("shape", frequency: 0.25f), builder.Constant(8.0f));
        var depth = builder.Interpolated(builder.Add(shape, builder.Mul(builder.Y, builder.Constant(-1.0f))));

        var graph = builder.Compile([shape, depth]);
        var direct = builder.Compile(shape);

        var celled = new DensitySampler(graph);
        var plain = new DensitySampler(direct);

        Assert.True(graph.RootIsCorner[0]);

        for (var z = -5; z <= 5; z += 3)
        {
            for (var x = -5; x <= 5; x += 3)
            {
                var position = new Vector3i(x, 3, z);

                Assert.Equal(plain.Sample(position, 0), celled.Sample(position, 0, 0));
            }
        }
    }

    [Fact]
    public void Sampler_SkipsABoxWhoseCornersAreAllAtOrBelowTheLevel()
    {
        var graph = HeightFieldGraph(interpolate: true);
        var scratch = new DensityScratch(graph);
        var sampler = new DensitySampler(graph, scratch);

        // The surface stays below 13.5 m and the box's lowest corners are at 24 m, so every corner is at least 10.5 m out of the ground:
        Assert.False(sampler.SampleRangeIfAbove(0, new Vector3i(-8, 100, -8), new Vector3i(7, 115, 7), -10.0f));
        Assert.True(sampler.SampleRangeIfAbove(0, new Vector3i(-8, 100, -8), new Vector3i(7, 115, 7), -30.0f));

        var min = new Vector3i(-8, 0, -8);
        var max = new Vector3i(7, 31, 7);

        Assert.True(sampler.SampleRangeIfAbove(0, min, max, -11.0f));

        var filled = scratch.RangeOutput(0).ToArray();
        var index = 0;

        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                for (var x = min.X; x <= max.X; x++)
                {
                    Assert.Equal(sampler.Sample(new Vector3i(x, y, z), 0), filled[index++]);
                }
            }
        }
    }

    [Fact]
    public void Sampler_ReusesItsBufferForASmallerBox()
    {
        var graph = HeightFieldGraph(interpolate: true);
        var scratch = new DensityScratch(graph);
        var sampler = new DensitySampler(graph, scratch);

        sampler.SampleRange(0, new Vector3i(-8, -4, -8), new Vector3i(7, 3, 7));

        var smallMin = new Vector3i(2, 0, 2);
        var smallMax = new Vector3i(4, 1, 4);

        sampler.SampleRange(0, smallMin, smallMax);

        var small = scratch.RangeOutput(0);
        var index = 0;

        for (var z = smallMin.Z; z <= smallMax.Z; z++)
        {
            for (var y = smallMin.Y; y <= smallMax.Y; y++)
            {
                for (var x = smallMin.X; x <= smallMax.X; x++)
                {
                    Assert.Equal(sampler.Sample(new Vector3i(x, y, z), 0), small[index++]);
                }
            }
        }
    }

    private static DensityGraph HeightFieldGraph(bool interpolate)
    {
        var builder = new DensityGraphBuilder(3141);
        var continents = builder.Noise2D("continents", frequency: 1.0f / 512.0f);
        var hills = builder.Noise2D("hills", frequency: 1.0f / 32.0f);

        var elevation = builder.Add(
            builder.Constant(4.0f),
            builder.Add(
                builder.Mul(continents, builder.Constant(8.0f)),
                builder.Mul(hills, builder.Constant(1.5f))
            )
        );

        var density = builder.Add(elevation, builder.Mul(builder.Y, builder.Constant(-1.0f)));

        return builder.Compile(interpolate ? builder.Interpolated(density) : density);
    }

    private static void AssertRangeMatchesPoints(DensitySampler sampler, Vector3i min, Vector3i maxInclusive)
    {
        var countX = maxInclusive.X - min.X + 1;
        var countY = maxInclusive.Y - min.Y + 1;
        var countZ = maxInclusive.Z - min.Z + 1;

        var density = Memory3D<float>.Allocate(countX, countY, countZ);
        sampler.SampleRange(0, min, maxInclusive, density.Span);
        var span = density.Span;

        for (var z = 0; z < countZ; z++)
        {
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    var position = new Vector3i(min.X + x, min.Y + y, min.Z + z);

                    Assert.Equal(sampler.Sample(position, 0), span[x, y, z]);
                }
            }
        }
    }

    private static float[,,] ReadRange(DensitySampler sampler, Vector3i min, Vector3i maxInclusive)
    {
        var countX = maxInclusive.X - min.X + 1;
        var countY = maxInclusive.Y - min.Y + 1;
        var countZ = maxInclusive.Z - min.Z + 1;

        var density = Memory3D<float>.Allocate(countX, countY, countZ);
        sampler.SampleRange(0, min, maxInclusive, density.Span);
        var span = density.Span;

        var result = new float[countX, countY, countZ];

        for (var z = 0; z < countZ; z++)
        {
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    result[x, y, z] = span[x, y, z];
                }
            }
        }

        return result;
    }
}

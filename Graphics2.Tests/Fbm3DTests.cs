using Graphics2.Mathematics.Noise;

namespace Graphics2.Tests;

public class Fbm3DTests
{
    private const float SampleStep = 0.17f;
    private const int SampleCount = 59;

    [Fact]
    public void Sample_IsAPureFunctionOfPositionAndParameters()
    {
        var noise = new ValueNoise3D(2024);
        var fbm = new Fbm3D<ValueNoise3D>(noise, 4);
        var sameParameters = new Fbm3D<ValueNoise3D>(new ValueNoise3D(2024), 4);
        var otherSeed = new Fbm3D<ValueNoise3D>(new ValueNoise3D(2025), 4);

        Assert.Equal(fbm.Sample(1.25f, -3.5f, 0.75f), sameParameters.Sample(1.25f, -3.5f, 0.75f));
        Assert.NotEqual(fbm.Sample(1.25f, -3.5f, 0.75f), otherSeed.Sample(1.25f, -3.5f, 0.75f));
        Assert.Equal(0.0f, default(Fbm3D<ValueNoise3D>).Sample(1.25f, -3.5f, 0.75f));
    }

    [Fact]
    public void Sample_WithOneOctave_IsTheBaseNoise()
    {
        var noise = new ValueNoise3D(11);
        var fbm = new Fbm3D<ValueNoise3D>(noise, 1, frequency: 3.0f);

        foreach (var (x, y, z) in new[] { (1.5f, -2.5f, 0.5f), (0.0f, 0.0f, 0.0f), (7.25f, 3.125f, -0.5f) })
        {
            Assert.Equal(noise.Sample(x * 3.0f, y * 3.0f, z * 3.0f), fbm.Sample(x, y, z), 1e-6f);
        }
    }

    [Fact]
    public void Sample_StaysInsideTheTheoreticalBound()
    {
        const int octaves = 5;
        const float gain = 0.5f;
        var amplitude = 0.0;
        var sumSquares = 0.0;
        var weight = 1.0;

        for (var i = 0; i < octaves; i++)
        {
            amplitude += weight;
            sumSquares += weight * weight;
            weight *= gain;
        }

        var bound = (float)(amplitude / Math.Sqrt(sumSquares));
        var fbm = new Fbm3D<ValueNoise3D>(new ValueNoise3D(99), octaves, gain: gain);

        for (var z = 0; z < 32; z++)
        {
            for (var y = 0; y < 32; y++)
            {
                for (var x = 0; x < 32; x++)
                {
                    var value = fbm.Sample(x * SampleStep, y * SampleStep, z * SampleStep);

                    Assert.InRange(value, -bound - 1e-5f, bound + 1e-5f);
                }
            }
        }
    }

    [Fact]
    public void Sample_KeepsTheAmplitudeOfTheBaseNoise()
    {
        var noise = new ValueNoise3D(2024);
        var fbm = new Fbm3D<ValueNoise3D>(noise, 4);
        var baseSquares = 0.0;
        var fbmSquares = 0.0;

        for (var z = 0; z < SampleCount; z++)
        {
            for (var y = 0; y < SampleCount; y++)
            {
                for (var x = 0; x < SampleCount; x++)
                {
                    var px = x * SampleStep;
                    var py = y * SampleStep;
                    var pz = z * SampleStep;

                    var baseValue = noise.Sample(px, py, pz);
                    var fbmValue = fbm.Sample(px, py, pz);

                    baseSquares += baseValue * baseValue;
                    fbmSquares += fbmValue * fbmValue;
                }
            }
        }

        var count = (double)SampleCount * SampleCount * SampleCount;
        var ratio = Math.Sqrt(fbmSquares / count) / Math.Sqrt(baseSquares / count);

        Assert.InRange(ratio, 0.9, 1.1);
    }

    [Fact]
    public void Sample_IsContinuousAcrossLatticePlanes()
    {
        var fbm = new Fbm3D<ValueNoise3D>(new ValueNoise3D(5), 4);
        const float epsilon = 1e-4f;

        foreach (var plane in new[] { 0.0f, -1.0f, 3.0f, -7.0f })
        {
            var xBelow = fbm.Sample(plane - epsilon, 0.3f, 0.6f);
            var xAbove = fbm.Sample(plane + epsilon, 0.3f, 0.6f);
            var yBelow = fbm.Sample(0.3f, plane - epsilon, 0.6f);
            var yAbove = fbm.Sample(0.3f, plane + epsilon, 0.6f);
            var zBelow = fbm.Sample(0.3f, 0.6f, plane - epsilon);
            var zAbove = fbm.Sample(0.3f, 0.6f, plane + epsilon);

            Assert.True(MathF.Abs(xAbove - xBelow) < 1e-2f, $"x jump at {plane}: {xBelow} -> {xAbove}");
            Assert.True(MathF.Abs(yAbove - yBelow) < 1e-2f, $"y jump at {plane}: {yBelow} -> {yAbove}");
            Assert.True(MathF.Abs(zAbove - zBelow) < 1e-2f, $"z jump at {plane}: {zBelow} -> {zAbove}");
        }
    }

    [Fact]
    public void Constructor_RejectsDegenerateParameters()
    {
        var noise = new ValueNoise3D(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => new Fbm3D<ValueNoise3D>(noise, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fbm3D<ValueNoise3D>(noise, 2, lacunarity: 0.0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fbm3D<ValueNoise3D>(noise, 2, gain: 0.0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fbm3D<ValueNoise3D>(noise, 2, frequency: 0.0f));
    }
}

using Graphics2.Mathematics.Noise;

namespace Graphics2.Tests;

public class ValueNoise3DTests
{
    [Fact]
    public void Sample_IsAPureFunctionOfPositionAndSeed()
    {
        var noise = new ValueNoise3D(1337);
        var sameSeed = new ValueNoise3D(1337);
        var otherSeed = new ValueNoise3D(1338);

        Assert.Equal(noise.Sample(1.25f, -3.5f, 0.75f), sameSeed.Sample(1.25f, -3.5f, 0.75f));

        var anyDifferent = false;

        for (var i = 0; i < 16; i++)
        {
            var x = i * 0.37f;
            var y = i * 0.11f;
            var z = i * 0.71f;

            anyDifferent |= noise.Sample(x, y, z) != otherSeed.Sample(x, y, z);
        }

        Assert.True(anyDifferent);
    }

    [Fact]
    public void Sample_StaysInsideTheUnitRange()
    {
        var noise = new ValueNoise3D(7);
        var min = float.MaxValue;
        var max = float.MinValue;

        for (var z = 0; z < 40; z++)
        {
            for (var y = 0; y < 40; y++)
            {
                for (var x = 0; x < 40; x++)
                {
                    var value = noise.Sample(x * 0.31f, y * 0.31f, z * 0.31f);

                    min = MathF.Min(min, value);
                    max = MathF.Max(max, value);
                }
            }
        }

        Assert.InRange(min, -1.0f, -0.5f);
        Assert.InRange(max, 0.5f, 1.0f);
    }

    [Fact]
    public void Sample_IsContinuousAcrossLatticePlanes()
    {
        var noise = new ValueNoise3D(3);
        const float epsilon = 1e-4f;

        foreach (var plane in new[] { 0.0f, -1.0f, 3.0f, -7.0f })
        {
            var xBelow = noise.Sample(plane - epsilon, 0.3f, 0.6f);
            var xAbove = noise.Sample(plane + epsilon, 0.3f, 0.6f);
            var yBelow = noise.Sample(0.3f, plane - epsilon, 0.6f);
            var yAbove = noise.Sample(0.3f, plane + epsilon, 0.6f);
            var zBelow = noise.Sample(0.3f, 0.6f, plane - epsilon);
            var zAbove = noise.Sample(0.3f, 0.6f, plane + epsilon);

            Assert.True(MathF.Abs(xAbove - xBelow) < 1e-2f, $"x jump at {plane}: {xBelow} -> {xAbove}");
            Assert.True(MathF.Abs(yAbove - yBelow) < 1e-2f, $"y jump at {plane}: {yBelow} -> {yAbove}");
            Assert.True(MathF.Abs(zAbove - zBelow) < 1e-2f, $"z jump at {plane}: {zBelow} -> {zAbove}");
        }
    }
}

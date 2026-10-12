using System.Runtime.CompilerServices;
using Graphics2.Mathematics;
using Graphics2.Mathematics.Interpolation;

namespace WinterGame.Materials;

/// <summary>
///     Noise on the unit torus. Coordinates are in tiles, and every function repeats with a period of 1 in both, so an image filled from it tiles.
///     A period is the number of lattice cells across the tile. Every function is a pure function of its arguments.
/// </summary>
public static class TileNoise
{
    private const int GradientCount = 256;
    private const float Sqrt2 = 1.41421356f;

    private static readonly float[] GradientX = CreateGradients(MathF.Cos);
    private static readonly float[] GradientY = CreateGradients(MathF.Sin);

    /// <summary>
    ///     Perlin gradient noise with <paramref name="period"/> cells across the tile, in [-1, 1].
    /// </summary>
    public static float Gradient(float u, float v, int period, int seed)
    {
        var x = u * period;
        var y = v * period;
        var floorX = MathF.Floor(x);
        var floorY = MathF.Floor(y);
        var fractionX = x - floorX;
        var fractionY = y - floorY;

        var x0 = Wrap((int)floorX, period);
        var y0 = Wrap((int)floorY, period);
        var x1 = x0 + 1 == period ? 0 : x0 + 1;
        var y1 = y0 + 1 == period ? 0 : y0 + 1;

        var g00 = Hash(x0, y0, seed) & (GradientCount - 1);
        var g10 = Hash(x1, y0, seed) & (GradientCount - 1);
        var g01 = Hash(x0, y1, seed) & (GradientCount - 1);
        var g11 = Hash(x1, y1, seed) & (GradientCount - 1);

        var n00 = GradientX[g00] * fractionX + GradientY[g00] * fractionY;
        var n10 = GradientX[g10] * (fractionX - 1.0f) + GradientY[g10] * fractionY;
        var n01 = GradientX[g01] * fractionX + GradientY[g01] * (fractionY - 1.0f);
        var n11 = GradientX[g11] * (fractionX - 1.0f) + GradientY[g11] * (fractionY - 1.0f);

        var tx = InterpolationFunctions.QuinticFadeInOut(fractionX);
        var ty = InterpolationFunctions.QuinticFadeInOut(fractionY);

        return Sqrt2 * MathX.Lerp(MathX.Lerp(n00, n10, tx), MathX.Lerp(n01, n11, tx), ty);
    }

    /// <summary>
    ///     A sum of <paramref name="octaves"/> gradient noises, each with twice the period of the one before and <paramref name="gain"/> times its amplitude.
    ///     The sum is divided by the total amplitude, so it stays in [-1, 1].
    /// </summary>
    public static float Fractal(float u, float v, int period, int octaves, float gain, int seed)
    {
        var sum = 0.0f;
        var amplitude = 1.0f;
        var total = 0.0f;

        for (var octave = 0; octave < octaves; octave++)
        {
            sum += amplitude * Gradient(u, v, period << octave, MathX.MixSeed(seed, octave));
            total += amplitude;
            amplitude *= gain;
        }

        return sum / total;
    }

    /// <summary>
    ///     Cellular noise over one feature point per cell, placed at the cell's centre plus a random offset of up to half of <paramref name="jitter"/> on each axis.
    ///     The distances are in cells.
    /// </summary>
    /// <param name="jitter">In [0, 1]. At 0 the points form a square grid.</param>
    public static CellularSample Cellular(float u, float v, int period, float jitter, int seed)
    {
        var x = u * period;
        var y = v * period;
        var floorX = MathF.Floor(x);
        var floorY = MathF.Floor(y);
        var cellX = (int)floorX;
        var cellY = (int)floorY;
        var fractionX = x - floorX;
        var fractionY = y - floorY;

        var nearestDistanceSquared = float.MaxValue;
        var nearestX = 0.0f;
        var nearestY = 0.0f;
        var nearestOffsetX = 0;
        var nearestOffsetY = 0;

        // The feature points are kept as vectors from the sample:
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                FeaturePoint(cellX + offsetX, cellY + offsetY, period, jitter, seed, out var pointX, out var pointY);
                pointX += offsetX - fractionX;
                pointY += offsetY - fractionY;

                var distanceSquared = pointX * pointX + pointY * pointY;

                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestX = pointX;
                    nearestY = pointY;
                    nearestOffsetX = offsetX;
                    nearestOffsetY = offsetY;
                }
            }
        }

        // The distance to the nearest cell edge is the least distance to the bisector between the nearest point and another:
        var edgeDistance = float.MaxValue;

        for (var offsetY = nearestOffsetY - 2; offsetY <= nearestOffsetY + 2; offsetY++)
        {
            for (var offsetX = nearestOffsetX - 2; offsetX <= nearestOffsetX + 2; offsetX++)
            {
                if (offsetX == nearestOffsetX && offsetY == nearestOffsetY)
                {
                    continue;
                }

                FeaturePoint(cellX + offsetX, cellY + offsetY, period, jitter, seed, out var pointX, out var pointY);
                pointX += offsetX - fractionX;
                pointY += offsetY - fractionY;

                var towardX = pointX - nearestX;
                var towardY = pointY - nearestY;
                var lengthSquared = towardX * towardX + towardY * towardY;

                if (lengthSquared < 1e-12f)
                {
                    continue;
                }

                var distance = 0.5f * ((nearestX + pointX) * towardX + (nearestY + pointY) * towardY) / MathF.Sqrt(lengthSquared);
                edgeDistance = MathF.Min(edgeDistance, distance);
            }
        }

        var cellHash = Hash(Wrap(cellX + nearestOffsetX, period), Wrap(cellY + nearestOffsetY, period), ~seed);

        return new CellularSample(MathF.Sqrt(nearestDistanceSquared), edgeDistance, ToUnit(cellHash));
    }

    /// <summary>
    ///     A random value in [0, 1) for the cell (<paramref name="x"/>, <paramref name="y"/>) of a lattice with <paramref name="period"/> cells across the tile, the same for every cell index that wraps to it.
    /// </summary>
    public static float CellValue(int x, int y, int period, int seed)
    {
        return ToUnit(Hash(Wrap(x, period), Wrap(y, period), seed));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FeaturePoint(int cellX, int cellY, int period, float jitter, int seed, out float x, out float y)
    {
        var hash = Hash(Wrap(cellX, period), Wrap(cellY, period), seed);

        x = 0.5f + jitter * ((hash & 0xFFFF) * (1.0f / 65536.0f) - 0.5f);
        y = 0.5f + jitter * ((hash >> 16) * (1.0f / 65536.0f) - 0.5f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Wrap(int cell, int period)
    {
        var remainder = cell % period;

        return remainder < 0 ? remainder + period : remainder;
    }

    /// <summary>
    ///     MurmurHash3 fmix32 over the combined inputs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(int x, int y, int seed)
    {
        var h = (uint)seed * 0x9E3779B1u;
        h ^= (uint)x * 0x85EBCA77u;
        h ^= (uint)y * 0xC2B2AE3Du;

        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;

        return h;
    }

    // The top 24 bits land exactly on floats, so the mapping is identical on any machine.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToUnit(uint hash)
    {
        return (hash >> 8) * (1.0f / 16777216.0f);
    }

    private static float[] CreateGradients(Func<float, float> component)
    {
        var gradients = new float[GradientCount];

        for (var index = 0; index < GradientCount; index++)
        {
            gradients[index] = component((index + 0.5f) * (2.0f * MathF.PI / GradientCount));
        }

        return gradients;
    }
}

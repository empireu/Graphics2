using System.Diagnostics;
using System.Runtime.CompilerServices;
// ReSharper disable ForCanBeConvertedToForeach

namespace Graphics2.Mathematics;

public static class MathX
{
    public const double SnzEpsilonDouble = 2.2e-15;
    public const float SnzEpsilonFloat = 1.2e-6f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Snz(double a) => a >= 0d ? 1 : -1;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Snz(float a) => a >= 0f ? 1 : -1;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Snz(int a) => a >= 0 ? 1 : -1;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NSnz(double a) => a <= 0d ? -1 : 1;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NSnz(float a) => a <= 0f ? -1 : 1;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int NSnz(int a) => a <= 0 ? -1 : 1;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SnzE(double a) => a >= 0.0 ? SnzEpsilonDouble : -SnzEpsilonDouble;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SnzE(float a) => a >= 0.0 ? SnzEpsilonFloat : -SnzEpsilonFloat;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Rsb(int a) => (a & -1) >>> 31;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Rsb2(int a)
    {
        unchecked
        {
            var x = (uint)a & -1;
            return (byte)(x >> 31);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    ///     Maps <see cref="v"/> from a source range to a destination range.
    /// </summary>
    /// <param name="v"></param>
    /// <param name="srcMin">The minimum value in <see cref="v"/>'s range.</param>
    /// <param name="srcMax">The maximum value in <see cref="v"/>'s range.</param>
    /// <param name="dstMin">The resulting range's minimum.</param>
    /// <param name="dstMax">The resulting range's maximum.</param>
    /// <returns>The mapped value. If <see cref="v"/> is outside the specified bounds, so will the result.</returns>
    public static float Map(float v, float srcMin, float srcMax, float dstMin, float dstMax)
    {
        return dstMin + (v - srcMin) * (dstMax - dstMin) / (srcMax - srcMin);
    }
    
    /// <summary>
    ///     Re-mapped smoothstep with the parameter <see cref="value"/> between <see cref="edge0"/> and <see cref="edge1"/>, clamped.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CheckedMapSmoothStepNonsingular(float edge0, float edge1, float value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);

        return t * t * (3.0f - 2.0f * t);
    }
    
    /// <summary>
    ///     Re-mapped smoothstep with the parameter <see cref="value"/> between <see cref="edge0"/> and <see cref="edge1"/>, clamped, and singularity removal for <see cref="edge0"/> ≈ <see cref="edge1"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CheckedMapSmoothStepSingular(float edge0, float edge1, float value)
    {
        var t = Math.Clamp(SnzE(value - edge0) / SnzE(edge1 - edge0), 0.0f, 1.0f);

        return t * t * (3.0f - 2.0f * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    
    /// <summary>
    ///     The coordinate of <paramref name="world"/> inside the block of edge <c>2^log</c> that contains it, in [0, 1 &lt;&lt; log).
    ///     Rounded down, so a negative world coordinate yields a non-negative local one, where the remainder operator would yield a negative index.
    ///     Pair it with <c>world &gt;&gt; log</c> for the block index. Requires 0 &lt;= log &lt;= 30.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReduceLocal(int world, int log)
    {
        var size = 1 << log;
        var v = world - size * (world >> log);
        var bit = v >> 31;
        return v + bit ^ bit;
    }

    /// <summary>
    ///     Division rounded toward negative infinity. The division operator truncates toward zero instead, so the two differ when the quotient is negative.
    ///     Requires a positive divisor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;

        return value % divisor != 0 && value < 0
            ? quotient - 1
            : quotient;
    }

    /// <summary>
    ///     Division rounded toward positive infinity, the counterpart of <see cref="FloorDiv"/>.
    ///     Requires a positive divisor, and the value cannot be <see cref="int.MinValue"/>, which has no positive counterpart to negate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CeilDiv(int value, int divisor) => -FloorDiv(-value, divisor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    
    /// <summary>
    ///     Applies <see cref="alignment"/> to the <see cref="value"/> (address or size).
    ///     The <see cref="alignment"/> must be power-of-two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignUp2(int value, int alignment)
    {
        Debug.Assert(IsPowerOfTwo(alignment), $"{nameof(AlignUp2)} takes a power-of-two alignment");
        
        return (value + (alignment - 1)) & ~(alignment - 1);
    }
    
    /// <summary>
    ///     Derives a distinct seed from a base seed and a salt.
    ///     A golden-ratio (Weyl) combine followed by a truncated lowbias32 avalanche, so nearby salts do not produce correlated fields.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MixSeed(int seed, int salt)
    {
        unchecked
        {
            var hash = ((uint)seed * 0x9E3779B1u) + (uint)salt;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;

            return (int)hash;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Fnv1A(string value)
    {
        ReadOnlySpan<char> str = value;
        
        unchecked
        {
            var hash = 2166136261u;

            for (var index = 0; index < str.Length; index++)
            {
                hash ^= str[index];
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }
}
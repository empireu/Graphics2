using System.Diagnostics;
using System.Runtime.CompilerServices;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge0 == edge1)
        {
            return value < edge0 ? 0.0f : 1.0f;
        }

        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);

        return t * t * (3.0f - (2.0f * t));
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
}
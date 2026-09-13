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
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReduceLocal(int world, int log)
    {
        var size = 1 << log;
        var v = world - size * (world >> log);
        var bit = v >> 31;
        return v + bit ^ bit;
    }
}
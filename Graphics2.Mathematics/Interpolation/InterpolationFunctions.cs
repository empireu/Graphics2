using System.Runtime.CompilerServices;

namespace Graphics2.Mathematics.Interpolation;

public static class InterpolationFunctions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float QuinticFadeInOut(float t)
    {
        return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
    }
}
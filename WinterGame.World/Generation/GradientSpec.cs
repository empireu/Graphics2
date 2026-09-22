using System.Runtime.CompilerServices;

namespace WinterGame.World.Generation;

/// <summary>
///     A linear map equivalent to <see cref="Graphics2.Mathematics.MathX.Map"/> with clamping.
/// </summary>
public readonly struct GradientSpec(float fromY, float toY, float fromValue, float toValue) : IEquatable<GradientSpec>
{
    public readonly float FromY = fromY;
    public readonly float ToY = toY;
    public readonly float FromValue = fromValue;
    public readonly float ToValue = toValue;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Evaluate(float y)
    {
        var t = Math.Clamp((y - FromY) / (ToY - FromY), 0.0f, 1.0f);

        return FromValue + (ToValue - FromValue) * t;
    }

    public bool Equals(GradientSpec other)
    {
        return FromY.Equals(other.FromY) && ToY.Equals(other.ToY) && FromValue.Equals(other.FromValue) && ToValue.Equals(other.ToValue);
    }

    public override bool Equals(object? obj)
    {
        return obj is GradientSpec other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(FromY, ToY, FromValue, ToValue);
    }

    public static bool operator ==(GradientSpec left, GradientSpec right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(GradientSpec left, GradientSpec right)
    {
        return !left.Equals(right);
    }
}

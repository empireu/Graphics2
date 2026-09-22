using static FastNoiseLite;

// ReSharper disable ConvertToPrimaryConstructor

namespace WinterGame.World.Generation;

/// <summary>
///     The configuration of one noise node, which is all the compiled graph keeps.
///     The sampler creates the noise generator from the spec.
/// </summary>
public readonly struct NoiseSpec : IEquatable<NoiseSpec>
{
    public readonly int Seed;
    public readonly NoiseType NoiseType;
    public readonly FractalType FractalType;
    public readonly int Octaves;

    /// <summary>
    ///     Cycles per meter.
    /// </summary>
    public readonly float Frequency;
    
    public NoiseSpec(int seed, NoiseType noiseType, FractalType fractalType, int octaves, float frequency)
    {
        Seed = seed;
        NoiseType = noiseType;
        FractalType = fractalType;
        Octaves = octaves;
        Frequency = frequency;
    }

    public bool Equals(NoiseSpec other)
    {
        return Seed == other.Seed && NoiseType == other.NoiseType && FractalType == other.FractalType && Octaves == other.Octaves && Frequency.Equals(other.Frequency);
    }

    public override bool Equals(object? obj)
    {
        return obj is NoiseSpec other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Seed, (int)NoiseType, (int)FractalType, Octaves, Frequency);
    }

    public static bool operator ==(NoiseSpec left, NoiseSpec right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(NoiseSpec left, NoiseSpec right)
    {
        return !left.Equals(right);
    }
}

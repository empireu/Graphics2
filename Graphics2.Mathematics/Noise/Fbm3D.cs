namespace Graphics2.Mathematics.Noise;

/// <summary>
///     Octave sum of a 3D noise primitive, the fractional Brownian motion shape, <c>sum(gain^i * noise(p * lacunarity^i))</c>.
///     The sum is normalized by the total octave RMS rather than by the total amplitude, so adding octaves adds detail without changing the large-scale amplitude.
///     The bound grows with the octave count, but the typical amplitude does not, which is why the world scale survives an octave change.
///     Every octave after the first samples through a fixed rotation, which decorrelates the octave lattices and removes the grid artifact that an axis-aligned stack produces.
///     A default instance is empty and samples 0.
/// </summary>
public readonly struct Fbm3D<TNoise> where TNoise : struct, INoise3D
{
    private const float R00 = 0.00f;
    private const float R01 = -0.80f;
    private const float R02 = -0.60f;
    private const float R10 = 0.80f;
    private const float R11 = 0.36f;
    private const float R12 = -0.48f;
    private const float R20 = 0.60f;
    private const float R21 = -0.48f;
    private const float R22 = 0.64f;

    private readonly TNoise _noise;
    private readonly int _octaves;
    private readonly float _lacunarity;
    private readonly float _gain;
    private readonly float _frequency;
    private readonly float _amplitude;

    public Fbm3D(TNoise noise, int octaves, float lacunarity = 2.0f, float gain = 0.5f, float frequency = 1.0f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(octaves, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lacunarity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gain);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        _noise = noise;
        _octaves = octaves;
        _lacunarity = lacunarity;
        _gain = gain;
        _frequency = frequency;

        var sumSquares = 0.0;
        var weight = 1.0;

        for (var i = 0; i < octaves; i++)
        {
            sumSquares += weight * weight;
            weight *= gain;
        }

        _amplitude = (float)(1.0 / Math.Sqrt(sumSquares));
    }

    public float Sample(float x, float y, float z)
    {
        var noise = _noise;
        var frequency = _frequency;
        var weight = 1.0f;
        var total = 0.0f;

        for (var i = 0; i < _octaves; i++)
        {
            total += weight * noise.Sample(x * frequency, y * frequency, z * frequency);

            var rotatedX = R00 * x + R01 * y + R02 * z;
            var rotatedY = R10 * x + R11 * y + R12 * z;
            var rotatedZ = R20 * x + R21 * y + R22 * z;

            x = rotatedX;
            y = rotatedY;
            z = rotatedZ;

            weight *= _gain;
            frequency *= _lacunarity;
        }

        return total * _amplitude;
    }
}
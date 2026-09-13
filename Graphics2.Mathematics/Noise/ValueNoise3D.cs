using System.Runtime.CompilerServices;
using Graphics2.Mathematics.Interpolation;

namespace Graphics2.Mathematics.Noise;

/// <summary>
///     Value noise on the integer lattice.
///     Works by hashing each of the eight corners of the containing cell to a value, then interpolating them with a quintic function.
///     The hash reads only the integer coordinates and the seed, so the field has no period and no state.
///     Output is in [-1, 1], with an RMS of roughly 0.40.
/// </summary>
public readonly struct ValueNoise3D(int seed) : INoise3D
{
    public float Sample(float x, float y, float z)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var iz = (int)MathF.Floor(z);

        // Zero in value and derivative at both ends, so the field is C2-continuous across lattice planes.
        var tx = InterpolationFunctions.QuinticFadeInOut(x - ix);
        var ty = InterpolationFunctions.QuinticFadeInOut(y - iy);
        var tz = InterpolationFunctions.QuinticFadeInOut(z - iz);

        var c000 = ToSigned(Hash(seed, ix, iy, iz));
        var c100 = ToSigned(Hash(seed, ix + 1, iy, iz));
        var c010 = ToSigned(Hash(seed, ix, iy + 1, iz));
        var c110 = ToSigned(Hash(seed, ix + 1, iy + 1, iz));
        var c001 = ToSigned(Hash(seed, ix, iy, iz + 1));
        var c101 = ToSigned(Hash(seed, ix + 1, iy, iz + 1));
        var c011 = ToSigned(Hash(seed, ix, iy + 1, iz + 1));
        var c111 = ToSigned(Hash(seed, ix + 1, iy + 1, iz + 1));

        var x00 = MathX.Lerp(c000, c100, tx);
        var x10 = MathX.Lerp(c010, c110, tx);
        var x01 = MathX.Lerp(c001, c101, tx);
        var x11 = MathX.Lerp(c011, c111, tx);

        return MathX.Lerp(
            MathX.Lerp(x00, x10, ty),
            MathX.Lerp(x01, x11, ty), 
            tz
        );
    }

    /// <summary>
    ///     MurmurHash3 fmix32.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(int seed, int x, int y, int z)
    {
        var h = (uint)seed * 0x9E3779B1u;
        h ^= (uint)x * 0x85EBCA77u;
        h ^= (uint)y * 0xC2B2AE3Du;
        h ^= (uint)z * 0x27D4EB2Fu;

        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;

        return h;
    }

    // The top 24 bits land exactly on floats, so the mapping is identical on any machine.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ToSigned(uint h)
    {
        return (h >> 8) * (1.0f / 8388608.0f) - 1.0f;
    }
}

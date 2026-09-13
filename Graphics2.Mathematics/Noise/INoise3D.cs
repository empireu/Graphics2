namespace Graphics2.Mathematics.Noise;

/// <summary>
///     3D noise primitive.
///     The implementations are a pure function of position, seed and construction parameters.
///     There is no mutable state, no allocation and no per-call seeding, so two holders of the same noise agree on every sample, which is what lets independently generated regions meet without a seam.
///     Output is bounded by [-1, 1], but the bound is only reached at the lattice corners. The RMS sits near 0.4, so consumers calibrate against that rather than against the bound.
///     Positions beyond 2^24 lose their fractional part in a float, so keeping sample coordinates local is the caller's responsibility.
/// </summary>
public interface INoise3D
{
    float Sample(float x, float y, float z);
}

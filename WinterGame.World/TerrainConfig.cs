namespace WinterGame.World;

/// <summary>
///     TerrainGenerator's parameters, every length in meters.
/// </summary>
public sealed class TerrainConfig
{
    public float BaseHeight { get; init; } = 4.0f;
    public float ContinentalWavelength { get; init; } = 512.0f;
    public float ContinentalAmplitude { get; init; } = 8.0f;
    public float HillWavelength { get; init; } = 32.0f;
    public float HillAmplitude { get; init; } = 1.5f;
    public float MountainWavelength { get; init; } = 96.0f;
    public float MountainAmplitude { get; init; } = 12.0f;
    public float MountainMaskMin { get; init; } = 0.05f;
    public float MountainMaskMax { get; init; } = 0.55f;

    /// <summary>
    ///     Content units per sample of distance, so the byte resolves <c>1 / ContentScale</c> of the sample spacing.
    /// </summary>
    public float ContentScale { get; init; } = 127.0f;
}

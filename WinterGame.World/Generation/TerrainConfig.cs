namespace WinterGame.World.Generation;

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

    /// <summary>
    ///     How far apart the geology provinces are.
    /// </summary>
    public float ProvinceWavelength { get; init; } = 512.0f;

    /// <summary>
    ///     The wavelength of the fold that warps the horizontal strata bands, so they are not perfectly flat across the world.
    /// </summary>
    public float FoldWavelength { get; init; } = 180.0f;

    /// <summary>
    ///     How far the fold moves a band, in meters.
    /// </summary>
    public float FoldAmplitude { get; init; } = 2.5f;

    /// <summary>
    ///     The depth of the snow shell below the surface, in meters.
    /// </summary>
    public float SnowThickness { get; init; } = 0.35f;

    /// <summary>
    ///     The thickness of the ice layer under the snow shell, in meters.
    /// </summary>
    public float IceThickness { get; init; } = 0.65f;

    /// <summary>
    ///     The province band tables the material root chooses among.
    /// </summary>
    public StrataSet Provinces { get; init; } = TerrainStrata.Provinces;
}

using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation;

/// <summary>
///     The parameters of <see cref="TerrainGraph"/>'s shape, rivers, ice and materials, in meters unless a property says otherwise.
///     The noises' values run over about [-1, 1], which is what the splines' control positions refer to.
/// </summary>
public sealed class TerrainConfig
{
    /// <summary>
    ///     The height every river and lowland lake is frozen up to. Every sample below it is solid, ground or ice.
    /// </summary>
    public float IceLevel { get; init; } = 0.0f;

    public float ContinentalWavelength { get; init; } = 2400.0f;

    /// <summary>
    ///     The base height above <see cref="IceLevel"/> by continentalness.
    /// </summary>
    public CubicSpline BaseHeight { get; init; } = new([-1.0f, -0.35f, 0.0f, 0.4f, 1.0f], [-8.0f, 2.0f, 12.0f, 40.0f, 70.0f]);

    public float ErosionWavelength { get; init; } = 1200.0f;

    /// <summary>
    ///     The mountains' height by erosion, before the continental mask. A negative value counts as zero.
    /// </summary>
    public CubicSpline MountainAmplitude { get; init; } = new([-1.0f, -0.3f, 0.3f, 1.0f], [140.0f, 70.0f, 15.0f, 0.0f]);

    public float MountainWavelength { get; init; } = 400.0f;

    // The continentalness over which the mountains fade in, so lowlands have none:
    public float MountainMaskMin { get; init; } = -0.2f;
    public float MountainMaskMax { get; init; } = 0.3f;

    public float HillWavelength { get; init; } = 160.0f;

    /// <summary>
    ///     The hills' amplitude by erosion. A negative value counts as zero.
    /// </summary>
    public CubicSpline HillAmplitude { get; init; } = new([-1.0f, 1.0f], [12.0f, 4.0f]);

    // The base height above the ice level over which the hills fade in, so a lowland lake has the continental noise's smooth outline:
    public float HillMaskMin { get; init; } = 0.0f;
    public float HillMaskMax { get; init; } = 12.0f;

    public float DetailWavelength { get; init; } = 32.0f;
    public float DetailAmplitude { get; init; } = 1.5f;

    /// <summary>
    ///     The wavelength of the noise whose zero line is a river's course, so rivers lie about half of it apart.
    /// </summary>
    public float RiverWavelength { get; init; } = 1600.0f;

    /// <summary>
    ///     The wavelength of the noises that vary a river's width and depth and its valley's walls along its course.
    /// </summary>
    public float RiverVariationWavelength { get; init; } = 500.0f;

    // The half-width of the channel the ice fills, and its depth below the ice level at the middle:
    public float RiverHalfWidthMin { get; init; } = 4.0f;
    public float RiverHalfWidthMax { get; init; } = 10.0f;
    public float RiverDepthMin { get; init; } = 1.5f;
    public float RiverDepthMax { get; init; } = 3.0f;

    // How far the valley reaches beside the channel per meter of the terrain's height above the ice level:
    public float ValleyRunMin { get; init; } = 1.5f;
    public float ValleyRunMax { get; init; } = 3.0f;

    /// <summary>
    ///     The wavelength of the noise that leaves the ice bare in patches.
    /// </summary>
    public float IceSnowWavelength { get; init; } = 24.0f;

    /// <summary>
    ///     The noise value above which snow lies on the ice, in [-1, 1], so a lower value covers more of it.
    /// </summary>
    public float IceSnowThreshold { get; init; } = -0.2f;

    /// <summary>
    ///     How deep the snow on the ice is where it lies.
    /// </summary>
    public float IceSnowDepth { get; init; } = 0.3f;

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

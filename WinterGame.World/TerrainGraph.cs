using Graphics2.Mathematics;
using WinterGame.World.Generation;

namespace WinterGame.World;

/// <summary>
///     The world's shape and material as one compiled graph with three roots.
///     The elevation root is coarse, so a point query takes one corner run and the shape program stays out of the sample program; the density and material roots share every subexpression they have in common.
/// </summary>
public sealed class TerrainGraph
{
    private const int ContinentalSalt = 0x1F123BB5;
    private const int HillSalt = 0x2A9F4B21;
    private const int MountainSalt = 0x6C8E9CF5;
    private const int ProvinceSalt = 0x51E7A33F;
    private const int FoldSalt = 0x3B0C6A9D;

    public readonly DensityGraph Graph;

    /// <summary>
    ///     The surface height in meters, point sampled and not interpolated.
    /// </summary>
    public readonly int ElevationRoot;

    /// <summary>
    ///     The signed distance to the surface in meters, positive inside.
    /// </summary>
    public readonly int DensityRoot;

    /// <summary>
    ///     The material id at the surface and below it, as <see cref="SurfaceMaterial" /> ids.
    /// </summary>
    public readonly int MaterialRoot;

    /// <summary>
    ///     The highest the surface can reach, which the sum of the shape's amplitudes bounds by construction.
    ///     A box entirely above it is air, and a feature's reach has to be added to it before a box is skipped.
    /// </summary>
    public readonly float MaxSurfaceY;

    private TerrainGraph(DensityGraph graph, int elevationRoot, int densityRoot, int materialRoot, float maxSurfaceY)
    {
        Graph = graph;
        ElevationRoot = elevationRoot;
        DensityRoot = densityRoot;
        MaterialRoot = materialRoot;
        MaxSurfaceY = maxSurfaceY;
    }

    public static TerrainGraph Build(int seed, TerrainConfig config, bool interpolate = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.MountainMaskMax <= config.MountainMaskMin)
        {
            throw new ArgumentException("The mountain mask's two edges have to increase.", nameof(config));
        }

        if (config.SnowThickness < 0.0f || config.IceThickness < 0.0f)
        {
            throw new ArgumentException("A skin thickness cannot be negative", nameof(config));
        }

        var builder = new DensityGraphBuilder(seed);

        var continental = builder.Noise2D("continents", ContinentalSalt, octaves: 4, frequency: 1.0f / config.ContinentalWavelength);
        var hills = builder.Noise2D("hills", HillSalt, octaves: 3, frequency: 1.0f / config.HillWavelength);

        var mountains = builder.Noise2D(
            "mountains",
            MountainSalt,
            fractalType: FastNoiseLite.FractalType.Ridged,
            octaves: 4,
            frequency: 1.0f / config.MountainWavelength
        );

        var elevation = builder.Add(
            builder.Constant(config.BaseHeight),
            builder.Add(
                builder.Mul(continental, builder.Constant(config.ContinentalAmplitude)),
                builder.Add(
                    builder.Mul(hills, builder.Constant(config.HillAmplitude)),
                    builder.Mul(
                        builder.Mul(
                            builder.Clamp(mountains, builder.Constant(0.0f), builder.Constant(1.0f)),
                            builder.Constant(config.MountainAmplitude)
                        ),
                        SmoothStep(builder, continental, config.MountainMaskMin, config.MountainMaskMax)
                    )
                )
            )
        );

        var density = builder.Add(elevation, builder.Mul(builder.Y, builder.Constant(-1.0f)));

        // The material root reads the same depth the shape root does, so the two cannot disagree about where the surface is:
        var depth = interpolate ? builder.Interpolated(density) : density;

        var province = builder.Noise2D("province", ProvinceSalt, octaves: 2, frequency: 1.0f / config.ProvinceWavelength);

        // The province coordinate runs over [0, province count], so its integer values are the boundaries the transition bands sit on:
        var provinceCoordinate = builder.Mul(
            builder.Clamp(
                builder.Mul(builder.Add(province, builder.Constant(1.0f)), builder.Constant(0.5f)),
                builder.Constant(0.0f),
                builder.Constant(1.0f)
            ),
            builder.Constant(config.Provinces.ProvinceCount)
        );

        var fold = builder.Noise2D("fold", FoldSalt, octaves: 2, frequency: 1.0f / config.FoldWavelength);
        var foldedY = builder.Add(builder.Y, builder.Mul(fold, builder.Constant(config.FoldAmplitude)));

        var bands = builder.BandedTable(foldedY, provinceCoordinate, config.Provinces);

        // The skin is depth relative and it is last, so it wins over the bands:
        var underSnow = builder.Select(
            builder.Subtract(depth, builder.Constant(config.SnowThickness)),
            builder.Constant(SurfaceMaterial.Ice),
            builder.Constant(SurfaceMaterial.Snow)
        );

        var material = builder.Select(
            builder.Subtract(depth, builder.Constant(config.SnowThickness + config.IceThickness)),
            bands,
            underSnow
        );

        var graph = builder.Compile([elevation, depth, material]);

        var maxSurfaceY = config.BaseHeight
            + config.ContinentalAmplitude
            + config.HillAmplitude
            + config.MountainAmplitude;

        return new TerrainGraph(graph, 0, 1, 2, maxSurfaceY);
    }

    /// <summary>
    ///     The Hermite smooth step <c>t * t * (3 - 2t)</c> over a clamped <c>t</c>, which is <see cref="MathX.CheckedMapSmoothStepNonsingular"/>.
    /// </summary>
    private static DensityNode SmoothStep(DensityGraphBuilder builder, DensityNode value, float edge0, float edge1)
    {
        var t = builder.Clamp(
            builder.Mul(builder.Add(value, builder.Constant(-edge0)), builder.Constant(1.0f / (edge1 - edge0))),
            builder.Constant(0.0f),
            builder.Constant(1.0f)
        );

        return builder.Mul(
            t,
            builder.Mul(t, builder.Add(builder.Constant(3.0f), builder.Mul(t, builder.Constant(-2.0f))))
        );
    }
}

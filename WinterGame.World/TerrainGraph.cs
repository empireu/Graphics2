using Graphics2.Mathematics;
using WinterGame.World.Generation;

namespace WinterGame.World;

public sealed class TerrainGraph
{
    private const int ContinentalSalt = 0x1F123BB5;
    private const int HillSalt = 0x2A9F4B21;
    private const int MountainSalt = 0x6C8E9CF5;

    /// <summary>
    ///     The surface height in meters, point sampled and not interpolated.
    /// </summary>
    public readonly DensityGraph Elevation;

    /// <summary>
    ///     The signed distance to the surface in meters, positive inside.
    /// </summary>
    public readonly DensityGraph Density;

    private TerrainGraph(DensityGraph elevation, DensityGraph density)
    {
        Elevation = elevation;
        Density = density;
    }

    public static TerrainGraph Build(int seed, TerrainConfig config, bool interpolate = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.MountainMaskMax <= config.MountainMaskMin)
        {
            throw new ArgumentException("The mountain mask's two edges have to increase.", nameof(config));
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

        return new TerrainGraph(
            builder.Compile(elevation),
            builder.Compile(interpolate ? builder.Interpolated(density) : density)
        );
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

using Graphics2.Mathematics;
using WinterGame.World.Generation.GraphProgram;

namespace WinterGame.World.Generation;

/// <summary>
///     The world's shape and material as one compiled graph with four roots, plus a small graph that bounds the surface of a column.
///     The shape is a height field from continentalness, erosion, mountains, hills and detail, cut by river valleys down to the ice level, and every sample below the ice level that the ground leaves empty is ice.
///     The elevation roots are coarse, so a point query takes one corner run and the shape program stays out of the sample program; the density and material roots share every subexpression they have in common.
/// </summary>
public sealed class TerrainGraph
{
    private const int ContinentalSalt = 0x1F123BB5;
    private const int HillSalt = 0x2A9F4B21;
    private const int MountainSalt = 0x6C8E9CF5;
    private const int ProvinceSalt = 0x51E7A33F;
    private const int FoldSalt = 0x3B0C6A9D;
    private const int ErosionSalt = 0x4D2B7E13;
    private const int DetailSalt = 0x7A1C5F29;
    private const int RiverSalt = 0x2E6F9B47;
    private const int RiverVariationSalt = 0x5B83D0C1;
    private const int ValleyRunSalt = 0x19C4A6E5;
    private const int IceSnowSalt = 0x6F3E2D87;

    /// <summary>
    ///     The river noise's gradient, in noise units per meter times its wavelength, below which the distance to its zero line stops growing, so a saddle of the noise does not widen a river without bound.
    /// </summary>
    private const float MinimumRiverGradient = 0.25f;

    public readonly DensityGraph Graph;

    /// <summary>
    ///     The height of the top of the solid in meters, the ground's or the ice's, point sampled and not interpolated.
    /// </summary>
    public readonly int ElevationRoot;

    /// <summary>
    ///     The signed distance to the surface in meters along the vertical, positive inside, interpolated.
    /// </summary>
    public readonly int DensityRoot;

    /// <summary>
    ///     The material id at the surface and below it, as <see cref="SurfaceMaterial" /> ids.
    /// </summary>
    public readonly int MaterialRoot;

    /// <summary>
    ///     The height of the ground in meters, under the ice where there is ice, point sampled and not interpolated.
    /// </summary>
    public readonly int GroundRoot;

    /// <summary>
    ///     The highest the surface can reach, which the sum of the shape's amplitudes bounds by construction.
    ///     A box entirely above it is air, and a feature's reach has to be added to it before a box is skipped.
    /// </summary>
    public readonly float MaxSurfaceY;

    /// <summary>
    ///     A graph with one root, a height in meters at a column that is at or above the top of the solid there before the rivers cut it.
    ///     The density root's corners on a column are its elevation less their height, so no sample of a cell is solid above the highest bound of its corner columns.
    ///     It runs only the shape's noises, so a loader can bound a column's surface without the rivers.
    /// </summary>
    public readonly DensityGraph SurfaceBoundGraph;

    private TerrainGraph(DensityGraph graph, DensityGraph surfaceBoundGraph, int elevationRoot, int densityRoot, int materialRoot, int groundRoot, float maxSurfaceY)
    {
        Graph = graph;
        SurfaceBoundGraph = surfaceBoundGraph;
        ElevationRoot = elevationRoot;
        DensityRoot = densityRoot;
        MaterialRoot = materialRoot;
        GroundRoot = groundRoot;
        MaxSurfaceY = maxSurfaceY;
    }

    public static TerrainGraph Build(int seed, TerrainConfig config, bool interpolate = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.MountainMaskMax <= config.MountainMaskMin || config.HillMaskMax <= config.HillMaskMin)
        {
            throw new ArgumentException("A mask's two edges have to increase.", nameof(config));
        }

        if (config.SnowThickness < 0.0f || config.IceThickness < 0.0f || config.IceSnowDepth < 0.0f)
        {
            throw new ArgumentException("A skin thickness cannot be negative", nameof(config));
        }

        if (config.RiverHalfWidthMin <= 0.0f || config.RiverHalfWidthMax < config.RiverHalfWidthMin || config.RiverDepthMax < config.RiverDepthMin || config.ValleyRunMax < config.ValleyRunMin)
        {
            throw new ArgumentException("A river's ranges have to be positive and ordered.", nameof(config));
        }

        var builder = new DensityGraphBuilder(seed);
        var zero = builder.Constant(0.0f);
        var minusY = builder.Mul(builder.Y, builder.Constant(-1.0f));
        var iceLevel = builder.Constant(config.IceLevel);

        var continental = builder.Noise2D("continents", ContinentalSalt, octaves: 3, frequency: 1.0f / config.ContinentalWavelength);
        var erosion = builder.Noise2D("erosion", ErosionSalt, octaves: 2, frequency: 1.0f / config.ErosionWavelength);
        var hills = builder.Noise2D("hills", HillSalt, octaves: 3, frequency: 1.0f / config.HillWavelength);
        var detail = builder.Noise2D("detail", DetailSalt, octaves: 2, frequency: 1.0f / config.DetailWavelength);

        var mountains = builder.Noise2D(
            "mountains",
            MountainSalt,
            fractalType: FastNoiseLite.FractalType.Ridged,
            octaves: 4,
            frequency: 1.0f / config.MountainWavelength
        );

        var mountainHeight = builder.Mul(
            builder.Mul(builder.Max(builder.Spline(erosion, config.MountainAmplitude), zero), SmoothStep(builder, continental, config.MountainMaskMin, config.MountainMaskMax)),
            builder.Clamp(mountains, zero, builder.Constant(1.0f))
        );

        var baseHeight = builder.Spline(continental, config.BaseHeight);

        var hillHeight = builder.Mul(
            builder.Mul(builder.Max(builder.Spline(erosion, config.HillAmplitude), zero), SmoothStep(builder, baseHeight, config.HillMaskMin, config.HillMaskMax)),
            hills
        );

        var raw = builder.Add(
            builder.Add(iceLevel, baseHeight),
            builder.Add(mountainHeight, builder.Add(hillHeight, builder.Mul(detail, builder.Constant(config.DetailAmplitude))))
        );

        var elevation = CutRiverValleys(builder, config, raw, iceLevel);
        var top = builder.Max(elevation, iceLevel);

        // The ground tells the ice fill from the ground, and the solid is the ground or the ice up to the ice level, which decides the surface:
        var groundValue = builder.Add(elevation, minusY);
        var contentValue = builder.Add(top, minusY);
        var ground = interpolate ? builder.Interpolated(groundValue) : groundValue;
        var content = interpolate ? builder.Interpolated(contentValue) : contentValue;

        var province = builder.Noise2D("province", ProvinceSalt, octaves: 2, frequency: 1.0f / config.ProvinceWavelength);

        // The province coordinate runs over [0, province count], so its integer values are the boundaries the transition bands sit on:
        var provinceCoordinate = builder.Mul(
            builder.Clamp(
                builder.Mul(builder.Add(province, builder.Constant(1.0f)), builder.Constant(0.5f)),
                zero,
                builder.Constant(1.0f)
            ),
            builder.Constant(config.Provinces.ProvinceCount)
        );

        var fold = builder.Noise2D("fold", FoldSalt, octaves: 2, frequency: 1.0f / config.FoldWavelength);
        var foldedY = builder.Add(builder.Y, builder.Mul(fold, builder.Constant(config.FoldAmplitude)));

        var bands = builder.BandedTable(foldedY, provinceCoordinate, config.Provinces);

        // The skin is measured along the normal below the top of the solid, so a steep face keeps its thickness and a bed under a meter of ice has none:
        var depth = interpolate ? builder.SurfaceDistance(content) : content;

        // The skin is depth relative and it is last, so it wins over the bands:
        var underSnow = builder.Select(
            builder.Subtract(depth, builder.Constant(config.SnowThickness)),
            builder.Constant(SurfaceMaterial.Ice),
            builder.Constant(SurfaceMaterial.Snow)
        );

        var skin = builder.Select(
            builder.Subtract(depth, builder.Constant(config.SnowThickness + config.IceThickness)),
            bands,
            underSnow
        );

        var depthBelowIceLevel = builder.Add(iceLevel, minusY);
        var iceSnow = builder.Noise2D("ice_snow", IceSnowSalt, octaves: 1, frequency: 1.0f / config.IceSnowWavelength);
        var snowDepth = builder.Select(builder.Subtract(iceSnow, builder.Constant(config.IceSnowThreshold)), builder.Constant(config.IceSnowDepth), zero);

        var fill = builder.Select(
            builder.Subtract(snowDepth, depthBelowIceLevel),
            builder.Constant(SurfaceMaterial.Snow),
            builder.Constant(SurfaceMaterial.Ice)
        );

        var material = builder.Select(ground, skin, fill);

        var graph = builder.Compile([top, content, material, elevation]);
        var surfaceBoundGraph = builder.Compile(builder.Max(raw, iceLevel));

        // Each amplitude below zero counts as zero, the mountain noise and its mask reach 1 and the others' noises 1 in magnitude, and the margin covers the splines' rounding:
        var maxSurfaceY = config.IceLevel
            + Math.Max(
                0.0f,
                config.BaseHeight.Maximum()
                + Math.Max(config.MountainAmplitude.Maximum(), 0.0f)
                + Math.Max(config.HillAmplitude.Maximum(), 0.0f)
                + Math.Abs(config.DetailAmplitude)
            )
            + 0.01f;

        return new TerrainGraph(graph, surfaceBoundGraph, 0, 1, 2, 3, maxSurfaceY);
    }

    /// <summary>
    ///     Lowers the raw height into a valley around each zero line of the river noise.
    ///     The distance to the line in meters is the noise over the length of its gradient, from central differences over a meter.
    ///     Inside the channel's half-width the bed is a parabola below the ice level, and beyond it the valley rises from the ice level to the raw height along a smooth step whose width grows with that height.
    /// </summary>
    private static DensityNode CutRiverValleys(DensityGraphBuilder builder, TerrainConfig config, DensityNode raw, DensityNode iceLevel)
    {
        var frequency = 1.0f / config.RiverWavelength;
        var zero = builder.Constant(0.0f);
        var one = builder.Constant(1.0f);
        var minusOne = builder.Constant(-1.0f);

        var river = builder.Noise2D("river", RiverSalt, octaves: 2, frequency: frequency);
        var east = builder.Noise2D("river_east", builder.Add(builder.X, one), builder.Z, RiverSalt, octaves: 2, frequency: frequency);
        var west = builder.Noise2D("river_west", builder.Add(builder.X, minusOne), builder.Z, RiverSalt, octaves: 2, frequency: frequency);
        var north = builder.Noise2D("river_north", builder.X, builder.Add(builder.Z, one), RiverSalt, octaves: 2, frequency: frequency);
        var south = builder.Noise2D("river_south", builder.X, builder.Add(builder.Z, minusOne), RiverSalt, octaves: 2, frequency: frequency);

        var half = builder.Constant(0.5f);
        var gradientX = builder.Mul(builder.Subtract(east, west), half);
        var gradientZ = builder.Mul(builder.Subtract(north, south), half);

        var gradient = builder.Max(
            builder.Sqrt(builder.Add(builder.Mul(gradientX, gradientX), builder.Mul(gradientZ, gradientZ))),
            builder.Constant(MinimumRiverGradient * frequency)
        );

        var distance = builder.Div(builder.Abs(river), gradient);

        var variation = UnitNoise(builder, "river_variation", RiverVariationSalt, config.RiverVariationWavelength);
        var halfWidth = Lerp(builder, config.RiverHalfWidthMin, config.RiverHalfWidthMax, variation);
        var depth = Lerp(builder, config.RiverDepthMin, config.RiverDepthMax, variation);
        var run = Lerp(builder, config.ValleyRunMin, config.ValleyRunMax, UnitNoise(builder, "valley_run", ValleyRunSalt, config.RiverVariationWavelength));

        var height = builder.Max(builder.Subtract(raw, iceLevel), zero);
        var valleyWidth = builder.Max(builder.Mul(height, run), builder.Constant(1.0e-3f));
        var acrossValley = builder.Clamp(builder.Div(builder.Subtract(distance, halfWidth), valleyWidth), zero, one);
        var valley = builder.Add(iceLevel, builder.Mul(height, SmoothStep(builder, acrossValley, 0.0f, 1.0f)));

        var acrossBed = builder.Div(distance, halfWidth);
        var bed = builder.Subtract(iceLevel, builder.Mul(depth, builder.Subtract(one, builder.Mul(acrossBed, acrossBed))));

        return builder.Min(raw, builder.Select(builder.Subtract(halfWidth, distance), bed, valley));
    }

    /// <summary>
    ///     A two-octave noise mapped from [-1, 1] to [0, 1] and clamped there.
    /// </summary>
    private static DensityNode UnitNoise(DensityGraphBuilder builder, string name, int salt, float wavelength)
    {
        var noise = builder.Noise2D(name, salt, octaves: 2, frequency: 1.0f / wavelength);

        return builder.Clamp(
            builder.Mul(builder.Add(noise, builder.Constant(1.0f)), builder.Constant(0.5f)),
            builder.Constant(0.0f),
            builder.Constant(1.0f)
        );
    }

    private static DensityNode Lerp(DensityGraphBuilder builder, float from, float to, DensityNode weight)
    {
        return builder.Add(builder.Constant(from), builder.Mul(weight, builder.Constant(to - from)));
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

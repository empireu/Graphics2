using Graphics2.Mathematics;

namespace WinterGame.Materials;

/// <summary>
///     The recipe of each material, building its maps at any power-of-two size.
///     A recipe is a surface function, which must repeat every tile, followed by passes over the whole planes.
///     Its lengths are in metres and its noise periods in cells per tile, so its look does not depend on the size.
/// </summary>
public static class MaterialRecipes
{
    /// <summary>
    ///     Pale limestone broken into blocks by joints, some of them open as cracks, with dissolution pits, iron staining and dark crevices.
    /// </summary>
    public static MaterialMaps Limestone(int size)
    {
        var planes = SurfacePlanes.Evaluate(size, LimestoneAt);
        planes.ShadeCavities(0.02f, 12.5f, 4.0f);
        planes.ShadeCavities(0.15f, 6.0f, 3.0f);

        return planes.Pack();
    }

    /// <summary>
    ///     Limestone at one point of the tile, before its cavities are shaded.
    /// </summary>
    internal static SurfacePoint LimestoneAt(float u, float v)
    {
        const int blockPeriod = 4;
        const float blockMetres = MaterialMaps.TileMetres / blockPeriod;

        // A broad warp bends the joints and a fine one roughens their edges:
        var warpU = u + 0.05f * TileNoise.Fractal(u, v, 2, 3, 0.5f, 101) + 0.012f * TileNoise.Fractal(u, v, 12, 4, 0.6f, 111);
        var warpV = v + 0.05f * TileNoise.Fractal(u, v, 2, 3, 0.5f, 102) + 0.012f * TileNoise.Fractal(u, v, 12, 4, 0.6f, 112);
        var blocks = TileNoise.Cellular(warpU, warpV, blockPeriod, 1.0f, 103);
        var jointMetres = blocks.EdgeDistance * blockMetres;

        // Only some joints are open, and an open joint varies in width:
        var openness = SmoothStep(0.0f, 0.3f, TileNoise.Fractal(u, v, 3, 3, 0.5f, 104));
        var crack = openness * (1.0f - SmoothStep(0.0f, 0.004f + 0.006f * openness, jointMetres));
        var seam = 1.0f - SmoothStep(0.0f, 0.006f, jointMetres);
        var lip = openness * Square(1.0f - SmoothStep(0.0f, 0.04f, jointMetres));

        // A finer joint set opens in fewer places:
        var fractures = TileNoise.Cellular(warpU, warpV, 8, 0.9f, 105);
        var fractureOpenness = SmoothStep(0.2f, 0.45f, TileNoise.Fractal(u, v, 4, 3, 0.5f, 106));
        var fracture = fractureOpenness * (1.0f - SmoothStep(0.0f, 0.004f, fractures.EdgeDistance * (MaterialMaps.TileMetres / 8)));

        // A pit is a dish in one region of a finer cellular noise in five:
        var pits = TileNoise.Cellular(warpU, warpV, 36, 0.9f, 107);
        var pitRadius = 0.1f + 1.0f * pits.RegionValue;
        var pit = pits.RegionValue < 0.2f ? MathF.Max(0.0f, 1.0f - Square(pits.Distance / pitRadius)) : 0.0f;

        var ridges = 1.0f - MathF.Abs(TileNoise.Fractal(u, v, 6, 4, 0.5f, 108));

        var height = 0.05f * TileNoise.Fractal(u, v, 4, 5, 0.5f, 109)
                     + 0.03f * ridges * ridges
                     + 0.06f * TileNoise.Fractal(u, v, 12, 4, 0.5f, 118)
                     + 0.03f * TileNoise.Fractal(u, v, 24, 4, 0.55f, 110)
                     + 0.005f * TileNoise.Fractal(u, v, 96, 3, 0.6f, 116)
                     + 0.016f * (blocks.RegionValue - 0.5f)
                     - 0.06f * crack
                     - 0.016f * lip
                     - 0.002f * seam
                     - 0.016f * fracture
                     - 0.012f * pit;

        // Weathering greys the pale stone in patches with ragged edges, and iron stains it ochre in smaller ones:
        var weathering = SmoothStep(0.0f, 0.15f, TileNoise.Fractal(u, v, 6, 5, 0.55f, 113) + 0.15f * TileNoise.Fractal(u, v, 48, 3, 0.5f, 117));
        var stain = 0.3f * SmoothStep(0.15f, 0.5f, TileNoise.Fractal(u, v, 8, 4, 0.5f, 114));
        var speckle = TileNoise.Gradient(u, v, 160, 115);
        var tone = (1.0f + 0.05f * (blocks.RegionValue - 0.5f)) * (1.0f + 0.07f * speckle) * (1.0f - 0.7f * crack) * (1.0f - 0.15f * seam - 0.4f * fracture);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(MathX.Lerp(0.48f, 0.39f, weathering), 0.38f, stain),
            tone * MathX.Lerp(MathX.Lerp(0.46f, 0.385f, weathering), 0.30f, stain),
            tone * MathX.Lerp(MathX.Lerp(0.41f, 0.36f, weathering), 0.20f, stain)
        );
    }

    /// <summary>
    ///     Wind-packed snow: ripples the wind left, stronger in some places than in others, over a fine crust, with almost no change in colour.
    /// </summary>
    public static MaterialMaps Snow(int size)
    {
        return SurfacePlanes.Evaluate(size, SnowAt).Pack();
    }

    /// <summary>
    ///     Snow at one point of the tile.
    /// </summary>
    internal static SurfacePoint SnowAt(float u, float v)
    {
        // The ripples run across the wind about 24 cm apart, rising slowly on the windward side and falling steeply on the lee:
        var phase = Fraction(16.0f * u + 5.0f * v + 0.8f * TileNoise.Fractal(u, v, 2, 2, 0.5f, 201));
        var ripple = phase < 0.65f ? SmoothStep(0.0f, 0.65f, phase) : 1.0f - SmoothStep(0.65f, 1.0f, phase);
        var strength = 0.6f + 0.4f * SmoothStep(-0.5f, 0.5f, TileNoise.Fractal(u, v, 4, 2, 0.5f, 202));

        // The swells are left to the terrain. Lumps of noise under the ripples, even 0.9 m ones, drew a grid with them in a low sun:
        var height = 0.012f * strength * ripple + 0.0015f * TileNoise.Fractal(u, v, 64, 3, 0.5f, 206);

        var tone = 1.0f + 0.025f * TileNoise.Fractal(u, v, 6, 3, 0.5f, 207) + 0.01f * TileNoise.Gradient(u, v, 64, 208);

        return new SurfacePoint(height, 0.785f * tone, 0.825f * tone, 0.885f * tone);
    }

    /// <summary>
    ///     Glacier ice: dark clear ice with pale layers of trapped bubbles along the rows, scattered bubbles, a few white fractures, and a surface scalloped by melting.
    /// </summary>
    public static MaterialMaps Ice(int size)
    {
        return SurfacePlanes.Evaluate(size, IceAt).Pack();
    }

    /// <summary>
    ///     Ice at one point of the tile.
    /// </summary>
    internal static SurfacePoint IceAt(float u, float v)
    {
        var warpU = u + 0.03f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 301);
        var warpV = v + 0.03f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 302);

        // The bubble layers follow the zero lines of a noise six times longer along u than across it, and fade out in places:
        var layerNoise = TileNoise.Fractal(warpU, 6.0f * warpV, 2, 3, 0.5f, 303);
        var layers = (1.0f - SmoothStep(0.0f, 0.12f, MathF.Abs(layerNoise))) * SmoothStep(-0.3f, 0.3f, TileNoise.Fractal(u, v, 5, 2, 0.5f, 311));

        // Bubbles 6 to 15 mm in radius, more of them in the layers:
        var bubbles = TileNoise.Cellular(warpU, warpV, 40, 0.9f, 304);
        var bubbleRadius = 0.06f + 0.09f * bubbles.RegionValue;
        var bubble = bubbles.RegionValue < 0.1f + 0.4f * layers ? 1.0f - SmoothStep(0.6f * bubbleRadius, bubbleRadius, bubbles.Distance) : 0.0f;

        var fractures = TileNoise.Cellular(warpU, warpV, 3, 1.0f, 305);
        var fractureOpenness = SmoothStep(0.1f, 0.35f, TileNoise.Fractal(u, v, 4, 3, 0.5f, 306));
        var fracture = fractureOpenness * (1.0f - SmoothStep(0.0f, 0.005f, fractures.EdgeDistance * (MaterialMaps.TileMetres / 3)));

        // Melting leaves shallow dishes with ridges between them:
        var scallops = TileNoise.Cellular(warpU, warpV, 14, 1.0f, 307);

        var height = 0.02f * Square(scallops.Distance)
                     + 0.02f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 308)
                     + 0.002f * TileNoise.Fractal(u, v, 48, 2, 0.5f, 309)
                     - 0.002f * fracture;

        var whiteness = Math.Clamp(0.45f * layers + 0.8f * bubble + 0.7f * fracture, 0.0f, 1.0f);
        var tone = 1.0f + 0.04f * TileNoise.Fractal(u, v, 8, 3, 0.5f, 310);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(0.27f, 0.58f, whiteness),
            tone * MathX.Lerp(0.36f, 0.66f, whiteness),
            tone * MathX.Lerp(0.41f, 0.70f, whiteness)
        );
    }

    private static float Fraction(float value)
    {
        return value - MathF.Floor(value);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        return MathX.CheckedMapSmoothStepNonsingular(edge0, edge1, value);
    }

    private static float Square(float value)
    {
        return value * value;
    }
}

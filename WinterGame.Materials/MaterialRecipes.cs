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
        planes.ShadeCavities(0.02f, 50.0f, 15.0f);
        planes.ShadeCavities(0.15f, 15.0f, 0.0f);

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
        var crack = openness * (1.0f - SmoothStep(0.0f, 0.003f + 0.012f * openness, jointMetres));
        var seam = 1.0f - SmoothStep(0.0f, 0.003f, jointMetres);
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

        var height = 0.025f * TileNoise.Fractal(u, v, 4, 5, 0.5f, 109)
                     + 0.015f * ridges * ridges
                     + 0.006f * TileNoise.Fractal(u, v, 24, 4, 0.55f, 110)
                     + 0.002f * TileNoise.Fractal(u, v, 96, 3, 0.6f, 116)
                     + 0.008f * (blocks.RegionValue - 0.5f)
                     - 0.03f * crack
                     - 0.008f * lip
                     - 0.001f * seam
                     - 0.008f * fracture
                     - 0.006f * pit;

        // Weathering greys the pale stone in patches with ragged edges, and iron stains it ochre in smaller ones:
        var weathering = SmoothStep(0.0f, 0.15f, TileNoise.Fractal(u, v, 6, 5, 0.55f, 113) + 0.15f * TileNoise.Fractal(u, v, 48, 3, 0.5f, 117));
        var stain = 0.3f * SmoothStep(0.15f, 0.5f, TileNoise.Fractal(u, v, 8, 4, 0.5f, 114));
        var speckle = TileNoise.Gradient(u, v, 160, 115);
        var tone = (1.0f + 0.05f * (blocks.RegionValue - 0.5f)) * (1.0f + 0.07f * speckle) * (1.0f - 0.7f * crack) * (1.0f - 0.25f * seam - 0.4f * fracture);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(MathX.Lerp(0.48f, 0.39f, weathering), 0.38f, stain),
            tone * MathX.Lerp(MathX.Lerp(0.46f, 0.385f, weathering), 0.30f, stain),
            tone * MathX.Lerp(MathX.Lerp(0.41f, 0.36f, weathering), 0.20f, stain)
        );
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

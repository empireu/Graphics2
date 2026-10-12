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
    ///     Wind-packed snow: soft hummocks under a fine crust, which the wind polished smooth in places, with almost no change in colour.
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
        // The wind turns over a winter, so nothing runs in one direction. The noise is rotated, because the aligned octaves of a plain fractal crust drew a grid in a low sun:
        var hummocks = TileNoise.RotatedFractal(u, v, 5, 3, 0.5f, 210);
        var polish = SmoothStep(-0.3f, 0.3f, TileNoise.RotatedFractal(u, v, 4, 2, 0.5f, 211));
        var crust = TileNoise.RotatedFractal(u, v, 32, 3, 0.5f, 206);
        var height = 0.008f * hummocks + (0.0018f - 0.0012f * polish) * crust;

        var tone = 1.0f + 0.025f * TileNoise.RotatedFractal(u, v, 6, 3, 0.5f, 207) + 0.01f * TileNoise.Gradient(u, v, 64, 208);

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

    /// <summary>
    ///     Warm sandstone in beds along the rows, each bed cross-bedded at its own angle and tinted on its own, with a fine grain and honeycomb hollows weathered into some beds.
    /// </summary>
    public static MaterialMaps Sandstone(int size)
    {
        var planes = SurfacePlanes.Evaluate(size, SandstoneAt);
        planes.ShadeCavities(0.02f, 12.5f, 4.0f);
        planes.ShadeCavities(0.15f, 6.0f, 3.0f);

        return planes.Pack();
    }

    /// <summary>
    ///     Sandstone at one point of the tile, before its cavities are shaded.
    /// </summary>
    internal static SurfacePoint SandstoneAt(float u, float v)
    {
        const int bedPeriod = 12;
        const float bedMetres = MaterialMaps.TileMetres / bedPeriod;

        var warpU = u + 0.03f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 401);
        var warpV = v + 0.03f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 402);

        // Beds 15 to 60 cm thick, whose boundaries wander a little. The thickness noise along v changes slower than the beds, so every bed keeps a thickness:
        var bedCoordinate = bedPeriod * v + 1.2f * TileNoise.Fractal(0.5f, v, 2, 2, 0.5f, 403) + 0.25f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 404);
        var bedFloor = MathF.Floor(bedCoordinate);
        var bed = (int)bedFloor;
        var boundaryMetres = MathF.Min(bedCoordinate - bedFloor, bedFloor + 1.0f - bedCoordinate) * bedMetres;
        var bedLean = TileNoise.CellValue(0, bed, bedPeriod, 405);
        var bedTint = TileNoise.CellValue(1, bed, bedPeriod, 406);
        var bedHeight = TileNoise.CellValue(2, bed, bedPeriod, 407);
        var bedHollows = TileNoise.CellValue(3, bed, bedPeriod, 408);
        var bedPlane = TileNoise.CellValue(4, bed, bedPeriod, 409);
        var bedLaminae = TileNoise.CellValue(5, bed, bedPeriod, 419);

        // A bed's laminae, 2 to 4 cm apart, lean by a whole number of cycles across the tile, up to about 25 degrees either way, and a third of the beds show none:
        var laminaCount = 100.0f + MathF.Round(100.0f * bedLaminae);
        var lean = MathF.Round((bedLean - 0.5f) * laminaCount);
        var laminaPhase = Fraction(laminaCount * v + lean * u + 0.3f * TileNoise.Fractal(u, v, 8, 2, 0.5f, 410));
        var lamina = (1.0f - MathF.Abs(2.0f * laminaPhase - 1.0f) - 0.5f) * SmoothStep(0.3f, 0.9f, bedLaminae);

        // Only some bedding planes are open, and every bed's edge is rounded:
        var groove = bedPlane < 0.6f ? 1.0f - SmoothStep(0.0f, 0.007f, boundaryMetres) : 0.0f;
        var bedEdge = SmoothStep(0.0f, 0.05f, boundaryMetres);

        // Honeycomb hollows in patches of some beds:
        var cells = TileNoise.Cellular(warpU, warpV, 24, 0.9f, 411);
        var hollowPatches = bedHollows < 0.35f ? SmoothStep(0.0f, 0.25f, TileNoise.Fractal(u, v, 4, 2, 0.5f, 412)) : 0.0f;
        var hollow = hollowPatches * MathF.Max(0.0f, 1.0f - Square(cells.Distance / 0.55f));

        var height = 0.04f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 413)
                     + 0.03f * TileNoise.Fractal(u, v, 10, 3, 0.5f, 414)
                     + 0.004f * TileNoise.Fractal(u, v, 48, 3, 0.5f, 415)
                     + 0.025f * (bedHeight - 0.5f) * bedEdge
                     - 0.012f * (1.0f - bedEdge)
                     + 0.0015f * lamina
                     - 0.005f * groove
                     - 0.016f * hollow;

        // Iron stains some patches a deeper red, and the grain speckles every surface:
        var stain = 0.5f * SmoothStep(0.1f, 0.45f, TileNoise.Fractal(u, v, 5, 4, 0.5f, 416));
        var grain = 0.06f * TileNoise.Gradient(u, v, 384, 417) + 0.04f * TileNoise.Gradient(u, v, 160, 418);
        var tone = (1.0f + 0.16f * lamina + grain) * (1.0f - 0.3f * groove);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(MathX.Lerp(0.48f, 0.40f, bedTint), 0.36f, stain),
            tone * MathX.Lerp(MathX.Lerp(0.37f, 0.26f, bedTint), 0.20f, stain),
            tone * MathX.Lerp(MathX.Lerp(0.24f, 0.14f, bedTint), 0.11f, stain)
        );
    }

    /// <summary>
    ///     Dark fissile shale: laminae along the rows grouped into plates of random thickness, each plate standing out by its own amount, flakes spalled off along the plates, and short joints across them.
    /// </summary>
    public static MaterialMaps Shale(int size)
    {
        var planes = SurfacePlanes.Evaluate(size, ShaleAt);
        planes.ShadeCavities(0.02f, 12.5f, 4.0f);
        planes.ShadeCavities(0.15f, 6.0f, 3.0f);

        return planes.Pack();
    }

    /// <summary>
    ///     Shale at one point of the tile, before its cavities are shaded.
    /// </summary>
    internal static SurfacePoint ShaleAt(float u, float v)
    {
        const int laminaPeriod = 128;
        const float laminaMetres = MaterialMaps.TileMetres / laminaPeriod;

        // Laminae about 3 cm thick, whose edges wander and are ragged at a finer scale:
        var laminaCoordinate = laminaPeriod * v + 2.0f * TileNoise.Fractal(0.5f, v, 8, 2, 0.5f, 501) + 0.8f * TileNoise.Fractal(u, v, 5, 2, 0.5f, 502) + 0.15f * TileNoise.Fractal(u, v, 64, 2, 0.5f, 503);
        var laminaFloor = MathF.Floor(laminaCoordinate);
        var lamina = (int)laminaFloor;
        var withinLamina = laminaCoordinate - laminaFloor;
        var edgeMetres = MathF.Min(withinLamina, 1.0f - withinLamina) * laminaMetres;

        // A third of the laminae part from the one below, so a plate is one to several laminae:
        var open = TileNoise.CellValue(0, lamina, laminaPeriod, 504) < 0.33f;
        var openBelow = TileNoise.CellValue(0, lamina + 1, laminaPeriod, 504) < 0.33f;
        var plateStart = lamina;

        for (var step = 0; step < 12 && TileNoise.CellValue(0, plateStart, laminaPeriod, 504) >= 0.33f; step++)
        {
            plateStart--;
        }

        var plateHeight = TileNoise.CellValue(1, plateStart, laminaPeriod, 505);
        var plateShift = 7.0f * TileNoise.CellValue(2, plateStart, laminaPeriod, 506);
        var plateTone = TileNoise.CellValue(3, plateStart, laminaPeriod, 507);

        // A shift along u is any number of tiles, so each plate reads its own stretch of one noise that repeats along u:
        var flake = SmoothStep(0.15f, 0.3f, TileNoise.Fractal(u + plateShift, 0.37f, 6, 3, 0.5f, 508) + 0.1f * TileNoise.Fractal(u, v, 40, 2, 0.5f, 509));
        var faceRelief = TileNoise.Fractal(u + plateShift, 0.71f, 3, 2, 0.5f, 510);

        var parting = (open && withinLamina < 0.5f) || (openBelow && withinLamina >= 0.5f) ? 1.0f - SmoothStep(0.0f, 0.005f, edgeMetres) : 0.0f;
        var seam = 1.0f - SmoothStep(0.0f, 0.003f, edgeMetres);

        // Joints cross the plates at right angles, in cells twice as tall as wide:
        var joints = TileNoise.Cellular(2.0f * u, v + 0.02f * TileNoise.Fractal(u, v, 6, 2, 0.5f, 511), 4, 1.0f, 512);
        var jointOpenness = SmoothStep(0.2f, 0.45f, TileNoise.Fractal(u, v, 4, 3, 0.5f, 513));
        var crack = jointOpenness * (1.0f - SmoothStep(0.0f, 0.004f, joints.EdgeDistance * (MaterialMaps.TileMetres / 8)));

        var height = 0.03f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 514)
                     + 0.012f * plateHeight
                     + 0.004f * faceRelief
                     - 0.005f * flake
                     - 0.004f * parting
                     - 0.001f * seam
                     - 0.015f * crack;

        // Weathering browns the stone in patches, and fresh faces where flakes fell are darker:
        var weathering = 0.4f * SmoothStep(0.1f, 0.4f, TileNoise.Fractal(u, v, 6, 4, 0.5f, 515));
        var tone = (1.0f + 0.16f * (plateTone - 0.5f) + 0.05f * TileNoise.Gradient(u, v, 256, 516)) * (1.0f - 0.12f * flake) * (1.0f - 0.35f * parting - 0.1f * seam) * (1.0f - 0.6f * crack);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(0.17f, 0.22f, weathering),
            tone * MathX.Lerp(0.19f, 0.19f, weathering),
            tone * MathX.Lerp(0.22f, 0.16f, weathering)
        );
    }

    /// <summary>
    ///     Very dark basalt: columns three times as tall along v as they are wide, split by joints that are open in most places, each column's face standing out by its own amount, pitted with gas vesicles and rusted in patches.
    /// </summary>
    public static MaterialMaps Basalt(int size)
    {
        var planes = SurfacePlanes.Evaluate(size, BasaltAt);
        planes.ShadeCavities(0.02f, 12.5f, 4.0f);
        planes.ShadeCavities(0.15f, 6.0f, 3.0f);

        return planes.Pack();
    }

    /// <summary>
    ///     Basalt at one point of the tile, before its cavities are shaded.
    /// </summary>
    internal static SurfacePoint BasaltAt(float u, float v)
    {
        const int columnPeriod = 4;

        var warpU = u + 0.015f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 601) + 0.004f * TileNoise.Fractal(u, v, 16, 3, 0.6f, 602);
        var warpV = v + 0.03f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 603) + 0.006f * TileNoise.Fractal(u, v, 16, 3, 0.6f, 604);

        // Columns 33 cm wide and 1 m tall on a wall, their joints open in most places:
        var columns = TileNoise.Cellular(3.0f * warpU, warpV, columnPeriod, 0.7f, 605);
        var jointMetres = columns.EdgeDistance * (MaterialMaps.TileMetres / (3 * columnPeriod));
        var jointOpenness = SmoothStep(-0.4f, 0.0f, TileNoise.Fractal(u, v, 5, 3, 0.5f, 606));
        var joint = jointOpenness * (1.0f - SmoothStep(0.0f, 0.003f + 0.004f * jointOpenness, jointMetres));
        var seam = 1.0f - SmoothStep(0.0f, 0.005f, jointMetres);
        var bevel = SmoothStep(0.0f, 0.012f, jointMetres);

        // Gas vesicles 4 to 12 mm in radius, crowded in some zones:
        var vesicles = TileNoise.Cellular(warpU, warpV, 72, 0.9f, 607);
        var crowding = SmoothStep(-0.3f, 0.4f, TileNoise.Fractal(u, v, 4, 3, 0.5f, 608));
        var vesicleRadius = 0.07f + 0.15f * vesicles.RegionValue;
        var vesicle = vesicles.RegionValue < 0.1f + 0.45f * crowding ? 1.0f - SmoothStep(0.7f * vesicleRadius, vesicleRadius, vesicles.Distance) : 0.0f;

        var height = 0.03f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 609)
                     + 0.025f * TileNoise.Fractal(u, v, 12, 3, 0.5f, 610)
                     + 0.005f * TileNoise.Fractal(u, v, 64, 3, 0.6f, 611)
                     + 0.024f * (columns.RegionValue - 0.5f)
                     - 0.006f * (1.0f - bevel)
                     - 0.025f * joint
                     - 0.002f * seam
                     - 0.006f * vesicle;

        // Rust stains some patches, a paler weathering rind dulls others, and light crystals speckle the stone:
        var rust = 0.4f * SmoothStep(0.2f, 0.5f, TileNoise.Fractal(u, v, 6, 4, 0.5f, 612));
        var rind = 0.5f * SmoothStep(0.0f, 0.4f, TileNoise.Fractal(u, v, 5, 3, 0.5f, 613));
        var crystals = SmoothStep(0.45f, 0.65f, TileNoise.Gradient(u, v, 384, 614));
        var tone = (1.0f + 0.12f * (columns.RegionValue - 0.5f) + 0.6f * crystals) * (1.0f - 0.6f * joint - 0.15f * seam) * (1.0f - 0.5f * vesicle);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(MathX.Lerp(0.085f, 0.13f, rind), 0.15f, rust),
            tone * MathX.Lerp(MathX.Lerp(0.088f, 0.13f, rind), 0.10f, rust),
            tone * MathX.Lerp(MathX.Lerp(0.10f, 0.14f, rind), 0.07f, rust)
        );
    }

    /// <summary>
    ///     Gneiss: pale and dark bands along the rows, folded by a strong warp, with crystals speckling both, the pale bands weathering out in relief, and a few joints.
    /// </summary>
    public static MaterialMaps Gneiss(int size)
    {
        var planes = SurfacePlanes.Evaluate(size, GneissAt);
        planes.ShadeCavities(0.02f, 12.5f, 4.0f);
        planes.ShadeCavities(0.15f, 6.0f, 3.0f);

        return planes.Pack();
    }

    /// <summary>
    ///     Gneiss at one point of the tile, before its cavities are shaded.
    /// </summary>
    internal static SurfacePoint GneissAt(float u, float v)
    {
        // The foliation is a coordinate across the bands, folded by a broad warp and crumpled by a finer one:
        var foliation = v + 0.05f * TileNoise.Fractal(u, v, 2, 3, 0.5f, 701) + 0.02f * TileNoise.Fractal(u, v, 6, 3, 0.5f, 702);

        // Bands from a centimetre to about 20 cm wide, read from one noise along the foliation:
        var banding = TileNoise.Fractal(0.37f, foliation, 24, 3, 0.65f, 703);
        var pale = SmoothStep(-0.12f, 0.0f, banding);
        var veins = 1.0f - SmoothStep(0.0f, 0.03f, MathF.Abs(TileNoise.Fractal(0.81f, foliation, 5, 2, 0.5f, 704) - 0.3f));

        // Crystals are stretched along the bands, dark mica in the pale bands and pale feldspar in the dark ones:
        var grainU = u + 0.004f * TileNoise.Gradient(u, v, 64, 705);
        var crystal = TileNoise.Gradient(grainU, 2.0f * foliation, 384, 706);
        var mica = SmoothStep(0.45f, 0.65f, crystal);
        var feldspar = SmoothStep(0.45f, 0.65f, -crystal);

        var warpU = u + 0.03f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 707);
        var warpV = v + 0.03f * TileNoise.Fractal(u, v, 3, 3, 0.5f, 708);
        var joints = TileNoise.Cellular(warpU, warpV, 3, 1.0f, 709);
        var jointOpenness = SmoothStep(0.15f, 0.4f, TileNoise.Fractal(u, v, 3, 3, 0.5f, 710));
        var crack = jointOpenness * (1.0f - SmoothStep(0.0f, 0.004f + 0.005f * jointOpenness, joints.EdgeDistance * (MaterialMaps.TileMetres / 3)));
        var seam = 1.0f - SmoothStep(0.0f, 0.006f, joints.EdgeDistance * (MaterialMaps.TileMetres / 3));

        var height = 0.04f * TileNoise.Fractal(u, v, 4, 3, 0.5f, 711)
                     + 0.03f * TileNoise.Fractal(u, v, 12, 3, 0.5f, 712)
                     + 0.004f * TileNoise.Fractal(u, v, 64, 3, 0.6f, 713)
                     + 0.016f * (joints.RegionValue - 0.5f)
                     + 0.004f * pale
                     + 0.002f * veins
                     - 0.04f * crack
                     - 0.002f * seam;

        var paleRed = MathX.Lerp(0.31f, 0.13f, mica);
        var paleGreen = MathX.Lerp(0.27f, 0.12f, mica);
        var paleBlue = MathX.Lerp(0.26f, 0.12f, mica);
        var darkRed = MathX.Lerp(0.125f, 0.24f, feldspar);
        var darkGreen = MathX.Lerp(0.12f, 0.22f, feldspar);
        var darkBlue = MathX.Lerp(0.125f, 0.21f, feldspar);
        var tone = (1.0f + 0.06f * (joints.RegionValue - 0.5f)) * (1.0f - 0.7f * crack - 0.15f * seam);

        return new SurfacePoint(
            height,
            tone * MathX.Lerp(MathX.Lerp(darkRed, paleRed, pale), 0.42f, veins),
            tone * MathX.Lerp(MathX.Lerp(darkGreen, paleGreen, pale), 0.40f, veins),
            tone * MathX.Lerp(MathX.Lerp(darkBlue, paleBlue, pale), 0.38f, veins)
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

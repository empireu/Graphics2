using System.Diagnostics;
using WinterGame.MaterialPreview;
using WinterGame.Materials;

var recipes = new Dictionary<string, Func<int, MaterialMaps>>(StringComparer.OrdinalIgnoreCase)
{
    ["limestone"] = MaterialRecipes.Limestone
};

var size = 1024;
var output = "material-preview";
var names = new List<string>();

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--size" when index + 1 < args.Length:
            size = int.Parse(args[++index]);
            break;
        case "--out" when index + 1 < args.Length:
            output = args[++index];
            break;
        default:
            names.Add(args[index]);
            break;
    }
}

if (names.Count == 0)
{
    names.AddRange(recipes.Keys);
}

Directory.CreateDirectory(output);
Console.WriteLine($"Material preview: {size}x{size} per tile, {MaterialMaps.TileMetres} m per tile, into {Path.GetFullPath(output)}");

foreach (var name in names)
{
    if (!recipes.TryGetValue(name, out var recipe))
    {
        Console.WriteLine($"  {name}: no such recipe. Known: {string.Join(", ", recipes.Keys)}");
        continue;
    }

    var stopwatch = Stopwatch.StartNew();
    var maps = recipe(size);
    var first = stopwatch.Elapsed.TotalMilliseconds;

    stopwatch.Restart();
    maps = recipe(size);
    var warm = stopwatch.Elapsed.TotalMilliseconds;

    Console.WriteLine($"  {name}: first bake {first:F0} ms, warm {warm:F0} ms");
    Preview.WriteAll(maps, Path.Combine(output, name.ToLowerInvariant()));
}

internal static class Preview
{
    /// <summary>
    ///     The direction toward the light in tile space (u right, v down, z up): from the upper left, 35 degrees above the surface.
    /// </summary>
    private static readonly (float X, float Y, float Z) Light = (-0.579f, -0.579f, 0.574f);

    private const float SunIrradiance = 1.6f;
    private const float SkyIrradiance = 0.35f;

    /// <summary>
    ///     The sky alone in the shade view, brought up as the game's exposure brings up a shaded scene.
    /// </summary>
    private const float ShadeSkyIrradiance = 1.6f;

    public static void WriteAll(MaterialMaps maps, string prefix)
    {
        var size = maps.Size;
        var albedo = new byte[3 * size * size];
        var height = new byte[size * size];
        var normal = new byte[3 * size * size];
        var occlusion = new byte[size * size];

        for (var index = 0; index < size * size; index++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                albedo[3 * index + channel] = maps.AlbedoHeight[4 * index + channel];
                normal[3 * index + channel] = maps.Normal[4 * index + channel];
            }

            height[index] = maps.AlbedoHeight[4 * index + 3];
            occlusion[index] = maps.Normal[4 * index + 3];
        }

        PngWriter.Write(prefix + "-albedo.png", size, size, 3, albedo);
        PngWriter.Write(prefix + "-height.png", size, size, 1, height);
        PngWriter.Write(prefix + "-normal.png", size, size, 3, normal);
        PngWriter.Write(prefix + "-occlusion.png", size, size, 1, occlusion);

        var lit = Shade(maps, SunIrradiance, SkyIrradiance);
        WriteTiled(prefix + "-lit.png", lit, size, 2, 1);
        WriteTiled(prefix + "-far.png", lit, size, 8, 4);
        WriteTiled(prefix + "-shade.png", Shade(maps, 0.0f, ShadeSkyIrradiance), size, 2, 1);
    }

    /// <summary>
    ///     The tile lit by a sun and a sky with plain Lambert shading, in linear light. The relief's occlusion darkens the sky.
    /// </summary>
    private static float[] Shade(MaterialMaps maps, float sunIrradiance, float skyIrradiance)
    {
        var size = maps.Size;
        var lit = new float[3 * size * size];

        for (var index = 0; index < size * size; index++)
        {
            var normalX = (maps.Normal[4 * index] - 128.0f) / 127.0f;
            var normalY = (maps.Normal[4 * index + 1] - 128.0f) / 127.0f;
            var normalZ = (maps.Normal[4 * index + 2] - 128.0f) / 127.0f;
            var inverseLength = 1.0f / MathF.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);

            var facing = MathF.Max(0.0f, (normalX * Light.X + normalY * Light.Y + normalZ * Light.Z) * inverseLength);
            var skyVisible = maps.Normal[4 * index + 3] / 255.0f;
            var irradiance = sunIrradiance * facing + skyIrradiance * skyVisible * (0.5f + 0.5f * normalZ * inverseLength);

            for (var channel = 0; channel < 3; channel++)
            {
                lit[3 * index + channel] = irradiance * DecodeSrgb(maps.AlbedoHeight[4 * index + channel]);
            }
        }

        return lit;
    }

    /// <summary>
    ///     Writes <paramref name="tiles"/> by <paramref name="tiles"/> copies of the tile, averaged down by <paramref name="reduction"/> on each axis.
    /// </summary>
    private static void WriteTiled(string path, float[] lit, int size, int tiles, int reduction)
    {
        var extent = size * tiles / reduction;
        var pixels = new byte[3 * extent * extent];
        var weight = 1.0f / (reduction * reduction);

        for (var y = 0; y < extent; y++)
        {
            for (var x = 0; x < extent; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0.0f;

                    for (var sampleY = 0; sampleY < reduction; sampleY++)
                    {
                        for (var sampleX = 0; sampleX < reduction; sampleX++)
                        {
                            var sourceX = (x * reduction + sampleX) % size;
                            var sourceY = (y * reduction + sampleY) % size;
                            sum += lit[3 * (sourceY * size + sourceX) + channel];
                        }
                    }

                    pixels[3 * (y * extent + x) + channel] = EncodeSrgb(sum * weight);
                }
            }
        }

        PngWriter.Write(path, extent, extent, 3, pixels);
    }

    private static float DecodeSrgb(byte encoded)
    {
        var value = encoded / 255.0f;

        return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static byte EncodeSrgb(float linear)
    {
        var clamped = Math.Clamp(linear, 0.0f, 1.0f);
        var encoded = clamped <= 0.0031308f ? 12.92f * clamped : 1.055f * MathF.Pow(clamped, 1.0f / 2.4f) - 0.055f;

        return (byte)(encoded * 255.0f + 0.5f);
    }
}

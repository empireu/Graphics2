using System.Diagnostics;
using Graphics2.Mathematics;
using WinterGame.MaterialPreview;
using WinterGame.Materials;

var recipes = new Dictionary<string, Func<int, MaterialMaps>>(StringComparer.OrdinalIgnoreCase)
{
    ["limestone"] = MaterialRecipes.Limestone,
    ["snow"] = MaterialRecipes.Snow,
    ["ice"] = MaterialRecipes.Ice,
    ["sandstone"] = MaterialRecipes.Sandstone,
    ["shale"] = MaterialRecipes.Shale,
    ["basalt"] = MaterialRecipes.Basalt,
    ["gneiss"] = MaterialRecipes.Gneiss
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

    var meanAlbedo = Preview.MeanAlbedo(maps);
    Console.WriteLine($"  {name}: first bake {first:F0} ms, warm {warm:F0} ms, mean linear albedo {meanAlbedo.Red:F3} {meanAlbedo.Green:F3} {meanAlbedo.Blue:F3}");
    Preview.WriteAll(maps, Path.Combine(output, name.ToLowerInvariant()));
}

internal static class Preview
{
    /// <summary>
    ///     The direction toward the light in tile space (u right, v down, z up): from the upper left, 35 degrees above the surface.
    /// </summary>
    private static readonly (float X, float Y, float Z) Light = (-0.579f, -0.579f, 0.574f);

    /// <summary>
    ///     About the game's noon: the sun's radiance and the sky's mean between its zenith and horizon.
    /// </summary>
    private const float SunRadiance = 0.72f;
    private const float SkyIrradiance = 0.37f;

    /// <summary>
    ///     The game's exposure: an image is brought half of the way from its mean log2 luminance to this key, in stops.
    /// </summary>
    private const float KeyEv = -1.3f;
    private const float Compensation = 0.5f;

    /// <summary>
    ///     The mean of the albedo in linear light, which a recipe aims near its palette colour.
    /// </summary>
    public static (float Red, float Green, float Blue) MeanAlbedo(MaterialMaps maps)
    {
        var sums = new double[3];

        for (var texel = 0; texel < maps.AlbedoHeight.Length; texel += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                sums[channel] += DecodeSrgb(maps.AlbedoHeight[texel + channel]);
            }
        }

        var count = maps.AlbedoHeight.Length / 4;

        return ((float)(sums[0] / count), (float)(sums[1] / count), (float)(sums[2] / count));
    }

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

        var lit = Shade(maps, SunRadiance, SkyIrradiance);
        WriteTiled(prefix + "-lit.png", lit, size, 2, 1);
        WriteTiled(prefix + "-far.png", lit, size, 8, 4);
        WriteTiled(prefix + "-shade.png", Shade(maps, 0.0f, SkyIrradiance), size, 2, 1);
    }

    /// <summary>
    ///     The tile lit by a sun and a sky with plain Lambert shading, then exposed as the game exposes a scene of it, in linear light. The relief's occlusion darkens the sky.
    /// </summary>
    private static float[] Shade(MaterialMaps maps, float sunRadiance, float skyIrradiance)
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
            var irradiance = sunRadiance * facing + skyIrradiance * skyVisible * (0.5f + 0.5f * normalZ * inverseLength);

            for (var channel = 0; channel < 3; channel++)
            {
                lit[3 * index + channel] = irradiance * DecodeSrgb(maps.AlbedoHeight[4 * index + channel]);
            }
        }

        var logSum = 0.0;

        for (var index = 0; index < size * size; index++)
        {
            var luminance = 0.2126f * lit[3 * index] + 0.7152f * lit[3 * index + 1] + 0.0722f * lit[3 * index + 2];
            logSum += MathF.Log2(MathF.Max(luminance, 1e-5f));
        }

        var exposure = MathF.Pow(2.0f, Compensation * (KeyEv - (float)(logSum / (size * size))));

        for (var index = 0; index < lit.Length; index++)
        {
            lit[index] *= exposure;
        }

        return lit;
    }

    /// <summary>
    ///     Writes <paramref name="tiles"/> by <paramref name="tiles"/> copies of the tile, averaged down by <paramref name="reduction"/> on each axis and tone mapped as the game does.
    /// </summary>
    private static void WriteTiled(string path, float[] lit, int size, int tiles, int reduction)
    {
        var extent = size * tiles / reduction;
        var pixels = new byte[3 * extent * extent];
        var weight = 1.0f / (reduction * reduction);
        Span<float> color = stackalloc float[3];

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

                    color[channel] = sum * weight;
                }

                PbrNeutral(color);

                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[3 * (y * extent + x) + channel] = EncodeSrgb(color[channel]);
                }
            }
        }

        PngWriter.Write(path, extent, extent, 3, pixels);
    }

    /// <summary>
    ///     Khronos PBR Neutral, as the game's tone mapper applies it.
    /// </summary>
    private static void PbrNeutral(Span<float> color)
    {
        const float startCompression = 0.8f - 0.04f;
        const float desaturation = 0.15f;

        var darkest = MathF.Min(color[0], MathF.Min(color[1], color[2]));
        var offset = darkest < 0.08f ? darkest - 6.25f * darkest * darkest : 0.04f;

        for (var channel = 0; channel < 3; channel++)
        {
            color[channel] -= offset;
        }

        var peak = MathF.Max(color[0], MathF.Max(color[1], color[2]));

        if (peak < startCompression)
        {
            return;
        }

        const float d = 1.0f - startCompression;
        var newPeak = 1.0f - d * d / (peak + d - startCompression);
        var whiteness = 1.0f - 1.0f / (desaturation * (peak - newPeak) + 1.0f);

        for (var channel = 0; channel < 3; channel++)
        {
            color[channel] = MathX.Lerp(color[channel] * newPeak / peak, newPeak, whiteness);
        }
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

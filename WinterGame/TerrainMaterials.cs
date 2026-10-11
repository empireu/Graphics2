using System.Diagnostics;
using System.Numerics;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using WinterGame.Materials;
using WinterGame.Utilities;
using WinterGame.World;

namespace WinterGame;

/// <summary>
///     The terrain's material maps as two 2D texture arrays with one layer per <see cref="SurfaceMaterial"/> id, each layer with its whole mip chain.
///     One array holds the albedo in sRGB with the height in alpha, the other the normal, as <see cref="MaterialMaps"/> lays them out.
///     A material with a recipe is baked from it, and one without is flat in its palette colour. The bake runs once per process on the thread pool, from the constructor, and <see cref="TryUpload"/> creates the textures on the render thread once it has finished.
/// </summary>
internal sealed class TerrainMaterials : IDisposable
{
    /// <summary>
    ///     The extent of a layer's largest level, which covers <see cref="MaterialMaps.TileMetres"/>.
    /// </summary>
    public const int Size = 512;

    private const TextureFormat AlbedoHeightFormat = TextureFormat.R8G8B8A8Srgb;
    private const TextureFormat NormalFormat = TextureFormat.R8G8B8A8Unorm;

    private static readonly uint LevelCount = MipChain.LevelCount(Size, Size);

    private static readonly LayerSource[] Layers = CreateLayers();

    private Task<BakedLayers>? _bake;
    private Texture? _albedoHeight;
    private Texture? _normal;
    private VkSampler? _sampler;

    public TerrainMaterials()
    {
        _bake = Task.Run(Bake);
    }

    public bool IsUploaded => _albedoHeight != null;

    public VkTexture AlbedoHeight => _albedoHeight ?? throw NotUploaded();

    public VkTexture Normal => _normal ?? throw NotUploaded();

    /// <summary>
    ///     Trilinear and anisotropic with repeat addressing, which both arrays are read through.
    /// </summary>
    public VkSampler Sampler => _sampler ?? throw NotUploaded();

    /// <summary>
    ///     Creates the textures once the bake has finished, and returns whether they exist. The upload waits for the GPU, once per process.
    /// </summary>
    public bool TryUpload(VulkanResourceFactory factory)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_bake == null)
        {
            return true;
        }

        if (!_bake.IsCompleted)
        {
            return false;
        }

        // A bake that threw is a bug, and its exception is rethrown here:
        var baked = _bake.GetAwaiter().GetResult();

        _albedoHeight = CreateArray(factory, AlbedoHeightFormat, baked.AlbedoHeight);
        _normal = CreateArray(factory, NormalFormat, baked.Normal);
        _sampler = factory.CreateSampler(new SamplerDescription());
        _bake = null;
        return true;
    }

    public void Dispose()
    {
        _albedoHeight?.Dispose();
        _normal?.Dispose();
        _sampler?.Dispose();
    }

    private static Texture CreateArray(VulkanResourceFactory factory, TextureFormat format, byte[] chains)
    {
        var texture = factory.CreateTexture(new TextureDescription
        {
            Width = Size,
            Height = Size,
            Format = format,
            Usage = TextureUsage.Sampled | TextureUsage.TransferDestination,
            MipLevels = LevelCount,
            ArrayLayers = SurfaceMaterial.Count,
            Type = TextureType.Texture2DArray
        });

        factory.UpdateTexture(texture, chains);
        return texture;
    }

    /// <summary>
    ///     Every layer's two mip chains, one layer per item of the thread pool.
    /// </summary>
    private static BakedLayers Bake()
    {
        var stopwatch = Stopwatch.StartNew();
        var layerSize = (int)MipChain.LayerSize(Size, Size, 1, LevelCount, AlbedoHeightFormat);
        var albedoHeight = new byte[layerSize * SurfaceMaterial.Count];
        var normal = new byte[layerSize * SurfaceMaterial.Count];

        Parallel.For(0, SurfaceMaterial.Count, id =>
        {
            var layer = Layers[id];
            var maps = layer.Recipe != null ? layer.Recipe(Size) : MaterialMaps.Flat(Size, layer.FlatAlbedo.X, layer.FlatAlbedo.Y, layer.FlatAlbedo.Z);
            var albedoHeightChain = albedoHeight.AsSpan(id * layerSize, layerSize);
            var normalChain = normal.AsSpan(id * layerSize, layerSize);

            maps.AlbedoHeight.CopyTo(albedoHeightChain);
            maps.Normal.CopyTo(normalChain);
            MipChain.Generate(albedoHeightChain, Size, Size, LevelCount, AlbedoHeightFormat);
            MipChain.Generate(normalChain, Size, Size, LevelCount, NormalFormat);
        });

        Console.WriteLine($"Terrain materials baked in {stopwatch.Elapsed.TotalMilliseconds:F0} ms");

        return new BakedLayers(albedoHeight, normal);
    }

    private static LayerSource[] CreateLayers()
    {
        var layers = new LayerSource[SurfaceMaterial.Count];

        layers[SurfaceMaterial.Rock] = new LayerSource(new Vector3(0.17f, 0.11f, 0.06f));
        layers[SurfaceMaterial.Ice] = new LayerSource(new Vector3(0.30f, 0.38f, 0.42f));
        layers[SurfaceMaterial.Snow] = new LayerSource(new Vector3(0.78f, 0.82f, 0.88f));
        layers[SurfaceMaterial.Sandstone] = new LayerSource(new Vector3(0.42f, 0.31f, 0.19f));
        layers[SurfaceMaterial.Shale] = new LayerSource(new Vector3(0.16f, 0.18f, 0.21f));
        layers[SurfaceMaterial.Limestone] = new LayerSource(new Vector3(0.47f, 0.46f, 0.42f), MaterialRecipes.Limestone);
        layers[SurfaceMaterial.Basalt] = new LayerSource(new Vector3(0.10f, 0.10f, 0.12f));
        layers[SurfaceMaterial.Gneiss] = new LayerSource(new Vector3(0.22f, 0.20f, 0.21f));

        return layers;
    }

    private static InvalidOperationException NotUploaded()
    {
        return new InvalidOperationException("The terrain materials are not uploaded yet.");
    }

    /// <summary>
    ///     A material's palette colour, its linear albedo while it has no <paramref name="Recipe"/> and the mean a recipe aims at, and its recipe if it has one.
    /// </summary>
    private readonly record struct LayerSource(Vector3 FlatAlbedo, Func<int, MaterialMaps>? Recipe = null);

    private sealed class BakedLayers(byte[] albedoHeight, byte[] normal)
    {
        public readonly byte[] AlbedoHeight = albedoHeight;
        public readonly byte[] Normal = normal;
    }
}

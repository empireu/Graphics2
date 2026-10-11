using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using StbImageSharp;

namespace Graphics2.Assets;

public static class ResourceFactoryExtensions
{
    /// <summary>
    ///     Loads an image as an sRGB texture with a complete mip chain, which is built on the CPU.
    /// </summary>
    public static Texture LoadTexture(this VulkanResourceFactory factory, Stream stream)
    {
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        var width = (uint)image.Width;
        var height = (uint)image.Height;
        var levels = MipChain.LevelCount(width, height);

        var chain = new byte[MipChain.LayerSize(width, height, 1, levels, TextureFormat.R8G8B8A8Srgb)];
        image.Data.CopyTo(chain, 0);
        MipChain.Generate(chain, width, height, levels, TextureFormat.R8G8B8A8Srgb);

        var texture = factory.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            MipLevels = levels,
            Format = TextureFormat.R8G8B8A8Srgb,
            Usage = TextureUsage.Sampled | TextureUsage.TransferDestination
        });

        factory.UpdateTexture(texture, chain);

        return texture;
    }
}

using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Graphics2.Assets;

public static class ResourceFactoryExtensions
{
    public static Texture LoadTexture(this VulkanResourceFactory factory, Stream stream)
    {
        using var image = Image.Load<Rgba32>(stream);

        var texture = factory.CreateTexture(new TextureDescription
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            Format = TextureFormat.R8G8B8A8Srgb,
            Usage = TextureUsage.Sampled | TextureUsage.TransferDestination
        });

        var pixelData = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixelData);

        factory.UpdateTexture(texture, pixelData);

        return texture;
    }
}

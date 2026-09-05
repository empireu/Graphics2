using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using StbImageSharp;

namespace Graphics2.Assets;

public static class ResourceFactoryExtensions
{
    public static Texture LoadTexture(this VulkanResourceFactory factory, Stream stream)
    {
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        var texture = factory.CreateTexture(new TextureDescription
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            Format = TextureFormat.R8G8B8A8Srgb,
            Usage = TextureUsage.Sampled | TextureUsage.TransferDestination
        });

        factory.UpdateTexture(texture, image.Data);

        return texture;
    }
}

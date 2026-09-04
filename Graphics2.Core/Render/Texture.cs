using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

public enum TextureLayout
{
    /// <summary>
    ///     Color attachment optimal. Image is written by a render pass.
    /// </summary>
    ColorAttachment,
    /// <summary>
    ///     Shader read only optimal. Image is sampled by a shader.
    /// </summary>
    ShaderRead,
    /// <summary>
    ///     Present source. Image is ready for presentation.
    /// </summary>
    Present,
    /// <summary>
    ///     Don't care. Contents are discarded (layout is undefined).
    /// </summary>
    None
}

public sealed class Texture
{
    internal readonly Image Image;
    internal readonly ImageView View;
    internal ImageLayout CurrentLayout;

    internal Texture(Image image, ImageView view, ImageLayout initialLayout)
    {
        Image = image;
        View = view;
        CurrentLayout = initialLayout;
    }
}
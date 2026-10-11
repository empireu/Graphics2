namespace Graphics2.Core.Resources;

/// <summary>
///     How many axes a texture has, which selects the image and view type.
/// </summary>
public enum TextureType
{
    Texture2D,
    Texture3D
}

public sealed class TextureDescription
{
    public required uint Width { get; init; }

    public required uint Height { get; init; }

    /// <summary>
    ///     Number of texels on the third axis of a <see cref="TextureType.Texture3D"/> image.
    ///     It is 1 for a <see cref="TextureType.Texture2D" /> image, where the third axis is a set of array layers instead.
    /// </summary>
    public uint Depth { get; init; } = 1;

    public required TextureFormat Format { get; init; }

    public required TextureUsage Usage { get; init; }

    public uint MipLevels { get; init; } = 1;

    public uint ArrayLayers { get; init; } = 1;

    /// <summary>
    ///     How many axes the image has.
    /// </summary>
    public TextureType Type { get; init; } = TextureType.Texture2D;

    /// <summary>
    ///     Throws when the description cannot describe a Vulkan image.
    ///     A 2D image has a depth of 1 and a 3D image has one array layer, because the third axis is either the volume's extent or a set of 2D layers, never both.
    ///     A render target has one mip level, because the texture's one view covers every level and a pass renders into one.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfZero(Width);
        ArgumentOutOfRangeException.ThrowIfZero(Height);
        ArgumentOutOfRangeException.ThrowIfZero(Depth);
        ArgumentOutOfRangeException.ThrowIfZero(MipLevels);
        ArgumentOutOfRangeException.ThrowIfZero(ArrayLayers);

        var completeChain = MipChain.LevelCount(Width, Height, Depth);

        if (MipLevels > completeChain)
        {
            throw new ArgumentOutOfRangeException(nameof(MipLevels), MipLevels, $"A {Width}x{Height}x{Depth} image has at most {completeChain} mip levels.");
        }

        if (MipLevels > 1 && (Usage & (TextureUsage.ColorAttachment | TextureUsage.DepthAttachment)) != 0)
        {
            throw new ArgumentException($"A render target has one mip level, but the description has {MipLevels}.", nameof(MipLevels));
        }

        if (Type == TextureType.Texture2D && Depth != 1)
        {
            throw new ArgumentException(
                $"A 2D image has a depth of 1, but the description has {Depth}. Use ArrayLayers for a set of 2D images.",
                nameof(Depth)
            );
        }

        if (Type == TextureType.Texture3D && ArrayLayers != 1)
        {
            throw new ArgumentException(
                $"A 3D image has one array layer, but the description has {ArrayLayers}. A volume cannot be an array as well.",
                nameof(ArrayLayers)
            );
        }
    }
}

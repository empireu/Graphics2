// ReSharper disable UnusedMember.Global

namespace Graphics2.Core.Resources;

public enum TextureFormat
{
    R8Unorm,
    R8Uint,
    R8G8B8A8Unorm,
    R8G8B8A8Srgb,
    B8G8R8A8Srgb,
    R32G32B32A32Sfloat,
    D32Sfloat,
    D24UnormS8Uint
}

public static class TextureFormatExtensions
{
    /// <summary>
    ///     Bytes per texel.
    ///     It is also the alignment a buffer offset has to be a multiple of when a copy writes into a texture of this format.
    /// </summary>
    public static int TexelSize(this TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => 1,
        TextureFormat.R8Uint => 1,
        TextureFormat.R8G8B8A8Unorm => 4,
        TextureFormat.R8G8B8A8Srgb => 4,
        TextureFormat.B8G8R8A8Srgb => 4,
        TextureFormat.R32G32B32A32Sfloat => 16,
        TextureFormat.D32Sfloat => 4,
        TextureFormat.D24UnormS8Uint => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported texture format.")
    };
}

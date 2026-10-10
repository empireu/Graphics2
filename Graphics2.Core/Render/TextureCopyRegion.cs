using Graphics2.Core.Resources;

namespace Graphics2.Core.Render;

/// <summary>
///     A box of texels that <see cref="Frame.CopyBufferToImage(RingAllocation, Texture, ReadOnlySpan{TextureCopyRegion})"/> writes into a texture.
///     Its texels are tightly packed in the source, starting <see cref="SourceOffset"/> bytes into the source allocation, with X fastest, then Y, then Z.
/// </summary>
public readonly struct TextureCopyRegion(uint sourceOffset, uint x, uint y, uint z, uint width, uint height, uint depth)
{
    public readonly uint SourceOffset = sourceOffset;
    public readonly uint X = x;
    public readonly uint Y = y;
    public readonly uint Z = z;
    public readonly uint Width = width;
    public readonly uint Height = height;
    public readonly uint Depth = depth;

    /// <summary>
    ///     Throws when the region is empty, reaches outside the texture or reads outside its source.
    ///     The source is <paramref name="sourceSize"/> bytes long and starts <paramref name="sourceStart"/> bytes into its buffer.
    ///     Vulkan needs each region to start in the buffer at a multiple of the texel size.
    /// </summary>
    public void Validate(uint textureWidth, uint textureHeight, uint textureDepth, TextureFormat format, uint sourceStart, uint sourceSize)
    {
        ArgumentOutOfRangeException.ThrowIfZero(Width);
        ArgumentOutOfRangeException.ThrowIfZero(Height);
        ArgumentOutOfRangeException.ThrowIfZero(Depth);

        if ((ulong)X + Width > textureWidth || (ulong)Y + Height > textureHeight || (ulong)Z + Depth > textureDepth)
        {
            throw new ArgumentException($"The region of {Width}x{Height}x{Depth} texels at ({X}, {Y}, {Z}) reaches outside the {textureWidth}x{textureHeight}x{textureDepth} texture.");
        }

        var texelSize = (ulong)format.TexelSize();
        var bufferOffset = (ulong)sourceStart + SourceOffset;

        if (bufferOffset % texelSize != 0)
        {
            throw new ArgumentException($"The region starts {bufferOffset} bytes into its buffer, which is not a multiple of the {texelSize} byte texel of {format}.");
        }

        var byteCount = (ulong)Width * Height * Depth * texelSize;

        if (SourceOffset + byteCount > sourceSize)
        {
            throw new ArgumentException($"The region reads {byteCount} bytes from {SourceOffset} bytes into its source, which is {sourceSize} bytes long.");
        }
    }
}

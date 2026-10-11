using Graphics2.Core.Resources;

namespace Graphics2.Core.Render;

/// <summary>
///     A box of texels that <see cref="Frame.CopyBufferToImage(RingAllocation, Texture, ReadOnlySpan{TextureCopyRegion})"/> writes into one mip level of one array layer of a texture.
///     Its texels are tightly packed in the source, starting <see cref="SourceOffset"/> bytes into the source allocation, with X fastest, then Y, then Z.
/// </summary>
public readonly struct TextureCopyRegion(uint sourceOffset, uint x, uint y, uint z, uint width, uint height, uint depth, uint mipLevel = 0, uint arrayLayer = 0)
{
    public readonly uint SourceOffset = sourceOffset;
    public readonly uint X = x;
    public readonly uint Y = y;
    public readonly uint Z = z;
    public readonly uint Width = width;
    public readonly uint Height = height;
    public readonly uint Depth = depth;
    public readonly uint MipLevel = mipLevel;
    public readonly uint ArrayLayer = arrayLayer;

    /// <summary>
    ///     Throws when the region is empty, reaches outside its level of the texture or reads outside its source.
    ///     The texture's extents are those of its first level. The source is <paramref name="sourceSize"/> bytes long and starts <paramref name="sourceStart"/> bytes into its buffer.
    ///     Vulkan needs each region to start in the buffer at a multiple of the texel size.
    /// </summary>
    public void Validate(uint textureWidth, uint textureHeight, uint textureDepth, uint mipLevels, uint arrayLayers, TextureFormat format, uint sourceStart, uint sourceSize)
    {
        ArgumentOutOfRangeException.ThrowIfZero(Width);
        ArgumentOutOfRangeException.ThrowIfZero(Height);
        ArgumentOutOfRangeException.ThrowIfZero(Depth);

        if (MipLevel >= mipLevels || ArrayLayer >= arrayLayers)
        {
            throw new ArgumentException($"The region writes level {MipLevel} of layer {ArrayLayer}, but the texture has {mipLevels} levels and {arrayLayers} layers.");
        }

        var levelWidth = MipChain.LevelExtent(textureWidth, MipLevel);
        var levelHeight = MipChain.LevelExtent(textureHeight, MipLevel);
        var levelDepth = MipChain.LevelExtent(textureDepth, MipLevel);

        if ((ulong)X + Width > levelWidth || (ulong)Y + Height > levelHeight || (ulong)Z + Depth > levelDepth)
        {
            throw new ArgumentException($"The region of {Width}x{Height}x{Depth} texels at ({X}, {Y}, {Z}) reaches outside level {MipLevel} of the texture, which is {levelWidth}x{levelHeight}x{levelDepth}.");
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

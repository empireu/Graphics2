using Graphics2.Core.Resources;

namespace Graphics2.Tests;

public class TextureDescriptionTests
{
    private static TextureDescription Create(TextureType type, uint depth, uint arrayLayers, uint width = 32, uint height = 32, uint mipLevels = 1, TextureUsage usage = TextureUsage.Sampled | TextureUsage.TransferDestination)
    {
        return new TextureDescription
        {
            Width = width,
            Height = height,
            Depth = depth,
            MipLevels = mipLevels,
            ArrayLayers = arrayLayers,
            Type = type,
            Format = TextureFormat.R8Uint,
            Usage = usage
        };
    }

    [Fact]
    public void Validate_AcceptsA2DImage()
    {
        Create(TextureType.Texture2D, 1, 1).Validate();
    }

    [Fact]
    public void Validate_AcceptsA2DArray()
    {
        Create(TextureType.Texture2D, 1, 6).Validate();
    }

    [Fact]
    public void Validate_AcceptsA3DVolume()
    {
        Create(TextureType.Texture3D, 36, 1).Validate();
    }

    [Fact]
    public void Validate_RejectsA2DImageWithDepth()
    {
        Assert.Throws<ArgumentException>(() => Create(TextureType.Texture2D, 36, 1).Validate());
    }

    [Fact]
    public void Validate_RejectsA3DVolumeWithArrayLayers()
    {
        Assert.Throws<ArgumentException>(() => Create(TextureType.Texture3D, 36, 4).Validate());
    }

    [Fact]
    public void Validate_RejectsAZeroExtent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(TextureType.Texture3D, 36, 1, width: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(TextureType.Texture3D, 0, 1).Validate());
    }

    [Fact]
    public void Validate_RejectsZeroMipLevels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(TextureType.Texture2D, 1, 1, mipLevels: 0).Validate());
    }

    [Fact]
    public void Validate_AcceptsACompleteChainAndRejectsALongerOne()
    {
        Create(TextureType.Texture2D, 1, 6, width: 64, height: 16, mipLevels: 7).Validate();
        Create(TextureType.Texture3D, 128, 1, width: 4, height: 4, mipLevels: 8).Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => Create(TextureType.Texture2D, 1, 6, width: 64, height: 16, mipLevels: 8).Validate());
    }

    [Fact]
    public void Validate_RejectsARenderTargetWithMipLevels()
    {
        Create(TextureType.Texture2D, 1, 1, usage: TextureUsage.ColorAttachment | TextureUsage.Sampled).Validate();

        Assert.Throws<ArgumentException>(() => Create(TextureType.Texture2D, 1, 1, mipLevels: 2, usage: TextureUsage.ColorAttachment | TextureUsage.Sampled).Validate());
        Assert.Throws<ArgumentException>(() => Create(TextureType.Texture2D, 1, 1, mipLevels: 2, usage: TextureUsage.DepthAttachment).Validate());
    }
}

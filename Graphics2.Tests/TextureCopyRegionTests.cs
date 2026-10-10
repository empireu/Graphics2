using Graphics2.Core.Render;
using Graphics2.Core.Resources;

namespace Graphics2.Tests;

public class TextureCopyRegionTests
{
    private const uint TextureWidth = 136;
    private const uint TextureHeight = 72;
    private const uint TextureDepth = 136;

    private static void Validate(TextureCopyRegion region, uint sourceSize, TextureFormat format = TextureFormat.R8Unorm, uint sourceStart = 0)
    {
        region.Validate(TextureWidth, TextureHeight, TextureDepth, format, sourceStart, sourceSize);
    }

    [Fact]
    public void Validate_AcceptsARegionThatEndsAtTheTextureAndSourceEdges()
    {
        Validate(new TextureCopyRegion(512, 128, 64, 128, 8, 8, 8), 1024);
    }

    [Fact]
    public void Validate_RejectsAZeroExtent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Validate(new TextureCopyRegion(0, 0, 0, 0, 0, 8, 8), 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => Validate(new TextureCopyRegion(0, 0, 0, 0, 8, 0, 8), 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => Validate(new TextureCopyRegion(0, 0, 0, 0, 8, 8, 0), 512));
    }

    [Fact]
    public void Validate_RejectsARegionReachingOutsideTheTexture()
    {
        Assert.Throws<ArgumentException>(() => Validate(new TextureCopyRegion(0, 129, 0, 0, 8, 8, 8), 512));
        Assert.Throws<ArgumentException>(() => Validate(new TextureCopyRegion(0, 0, 65, 0, 8, 8, 8), 512));
        Assert.Throws<ArgumentException>(() => Validate(new TextureCopyRegion(0, 0, 0, 129, 8, 8, 8), 512));
    }

    [Fact]
    public void Validate_RejectsARegionReadingPastTheEndOfItsSource()
    {
        Assert.Throws<ArgumentException>(() => Validate(new TextureCopyRegion(513, 0, 0, 0, 8, 8, 8), 1024));
    }

    [Fact]
    public void Validate_RejectsABufferOffsetThatIsNotAMultipleOfTheTexelSize()
    {
        Validate(new TextureCopyRegion(12, 0, 0, 0, 1, 1, 1), 28, TextureFormat.R32G32B32A32Sfloat, 4);

        Assert.Throws<ArgumentException>(() => Validate(new TextureCopyRegion(0, 0, 0, 0, 1, 1, 1), 16, TextureFormat.R32G32B32A32Sfloat, 4));
    }
}

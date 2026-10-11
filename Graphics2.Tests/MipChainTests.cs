using Graphics2.Core.Resources;

namespace Graphics2.Tests;

public class MipChainTests
{
    [Fact]
    public void LevelCount_EndsAtOneTexelOnTheLongestAxis()
    {
        Assert.Equal(1u, MipChain.LevelCount(1, 1));
        Assert.Equal(6u, MipChain.LevelCount(32, 32));
        Assert.Equal(10u, MipChain.LevelCount(512, 256));
        Assert.Equal(10u, MipChain.LevelCount(3, 600));
        Assert.Equal(8u, MipChain.LevelCount(4, 4, 128));
    }

    [Fact]
    public void LevelExtent_HalvesAndStopsAtOne()
    {
        Assert.Equal(68u, MipChain.LevelExtent(136, 1));
        Assert.Equal(1u, MipChain.LevelExtent(136, 8));
        Assert.Equal(1u, MipChain.LevelExtent(136, 40));
        Assert.Equal(5u, MipChain.LevelExtent(11, 1));
    }

    [Fact]
    public void LayerSize_SumsTheLevels()
    {
        Assert.Equal((16 + 4 + 1) * 4L, MipChain.LayerSize(4, 4, 1, 3, TextureFormat.R8G8B8A8Unorm));
        Assert.Equal(8 + 4 + 2 + 1L, MipChain.LayerSize(8, 1, 1, 4, TextureFormat.R8Unorm));
        Assert.Equal(16L, MipChain.LayerSize(4, 4, 1, 1, TextureFormat.R8Unorm));
    }

    [Fact]
    public void Generate_AveragesEachTwoByTwoBlockWithRounding()
    {
        var chain = new byte[(int)MipChain.LayerSize(4, 2, 1, 3, TextureFormat.R8Unorm)];
        byte[] first = [10, 20, 0, 0, 30, 41, 255, 255];
        first.CopyTo(chain, 0);

        MipChain.Generate(chain, 4, 2, 3, TextureFormat.R8Unorm);

        Assert.Equal(new byte[] { 10, 20, 0, 0, 30, 41, 255, 255, 25, 128, 77 }, chain);
    }

    [Fact]
    public void Generate_AveragesSrgbColorInLinearLightAndAlphaAsStored()
    {
        var chain = new byte[(int)MipChain.LayerSize(2, 2, 1, 2, TextureFormat.R8G8B8A8Srgb)];
        byte[] first = [0, 0, 255, 0, 0, 0, 255, 0, 255, 0, 255, 255, 255, 0, 255, 255];
        first.CopyTo(chain, 0);

        MipChain.Generate(chain, 2, 2, 2, TextureFormat.R8G8B8A8Srgb);

        Assert.Equal(new byte[] { 188, 0, 255, 128 }, chain[16..]);
    }

    [Fact]
    public void Generate_RejectsAnIntegerFormat()
    {
        var chain = new byte[3];

        Assert.Throws<ArgumentException>(() => MipChain.Generate(chain, 2, 1, 2, TextureFormat.R8Uint));
    }
}

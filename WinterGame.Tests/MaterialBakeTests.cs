using WinterGame.Materials;

namespace WinterGame.Tests;

public class MaterialBakeTests
{
    [Fact]
    public void NoiseRepeatsEveryTile()
    {
        var random = new Random(1);

        for (var sample = 0; sample < 200; sample++)
        {
            var u = random.NextSingle();
            var v = random.NextSingle();

            Assert.Equal(TileNoise.Gradient(u, v, 5, 7), TileNoise.Gradient(u + 1.0f, v - 1.0f, 5, 7), 1e-4f);
            Assert.Equal(TileNoise.Fractal(u, v, 3, 4, 0.5f, 7), TileNoise.Fractal(u - 1.0f, v + 1.0f, 3, 4, 0.5f, 7), 1e-4f);
            Assert.Equal(
                TileNoise.RotatedFractal(u, v, 3, TileNoise.MaxRotatedOctaves, 0.7f, 7),
                TileNoise.RotatedFractal(u + 1.0f, v - 1.0f, 3, TileNoise.MaxRotatedOctaves, 0.7f, 7),
                1e-4f);

            var cellular = TileNoise.Cellular(u, v, 6, 1.0f, 7);
            var shifted = TileNoise.Cellular(u + 1.0f, v + 1.0f, 6, 1.0f, 7);

            Assert.Equal(cellular.Distance, shifted.Distance, 1e-4f);
            Assert.Equal(cellular.EdgeDistance, shifted.EdgeDistance, 1e-4f);
            Assert.Equal(cellular.RegionValue, shifted.RegionValue);
        }

        Assert.Equal(TileNoise.CellValue(3, -2, 7, 9), TileNoise.CellValue(10, 5, 7, 9));
        Assert.NotEqual(TileNoise.CellValue(3, -2, 7, 9), TileNoise.CellValue(4, -2, 7, 9));
    }

    [Fact]
    public void EdgeDistanceIsTheDistanceToTheBorder()
    {
        const int period = 6;
        const int directions = 32;
        var random = new Random(2);

        for (var sample = 0; sample < 40; sample++)
        {
            var u = random.NextSingle();
            var v = random.NextSingle();
            var start = TileNoise.Cellular(u, v, period, 1.0f, 3);
            var nearestExit = float.MaxValue;

            // A region is convex, so each ray leaves it once. The exit is found by steps of 0.05 cells, then bisected:
            for (var direction = 0; direction < directions; direction++)
            {
                var angle = 2.0f * MathF.PI * direction / directions;
                var stepU = MathF.Cos(angle) / period;
                var stepV = MathF.Sin(angle) / period;
                var inside = 0.0f;
                var outside = 0.05f;

                while (TileNoise.Cellular(u + outside * stepU, v + outside * stepV, period, 1.0f, 3).RegionValue == start.RegionValue)
                {
                    inside = outside;
                    outside += 0.05f;
                }

                for (var iteration = 0; iteration < 20; iteration++)
                {
                    var middle = 0.5f * (inside + outside);

                    if (TileNoise.Cellular(u + middle * stepU, v + middle * stepV, period, 1.0f, 3).RegionValue == start.RegionValue)
                    {
                        inside = middle;
                    }
                    else
                    {
                        outside = middle;
                    }
                }

                nearestExit = MathF.Min(nearestExit, outside);
            }

            // The nearest of the rays' exits lies at most 1 / cos(pi / 32) times the true distance:
            Assert.True(start.EdgeDistance <= nearestExit + 1e-3f, $"{start.EdgeDistance} against a nearest exit at {nearestExit}");
            Assert.True(nearestExit <= 1.006f * start.EdgeDistance + 1e-3f, $"{start.EdgeDistance} against a nearest exit at {nearestExit}");
        }
    }

    [Fact]
    public void BlurKeepsTheSumAndWraps()
    {
        var image = new TileImage(32);
        image[0, 0] = 1.0f;

        var blurred = TileOps.Blur(image, 2);

        Assert.Equal(1.0f, blurred.Pixels.Sum(), 1e-4f);
        Assert.True(blurred[31, 31] > 0.0f);
        Assert.Equal(0.0f, blurred[16, 16], 1e-6f);
    }

    [Fact]
    public void NormalsLeanAwayFromTheRise()
    {
        const int size = 64;
        var flat = new TileImage(size);
        var height = new TileImage(size);

        // The height rises along u at x = 0 and falls at x = size / 2, and does the same along v:
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                height[x, y] = 0.2f * (MathF.Sin(2.0f * MathF.PI * x / size) + MathF.Sin(2.0f * MathF.PI * y / size));
            }
        }

        var maps = MaterialMaps.Pack(flat, flat, flat, height);

        Assert.True(NormalAt(maps, 0, 8, 0) < 120);
        Assert.True(NormalAt(maps, size / 2, 8, 0) > 136);
        Assert.True(NormalAt(maps, 8, 0, 1) < 120);
        Assert.True(NormalAt(maps, 8, size / 2, 1) > 136);
        Assert.True(NormalAt(maps, 8, 8, 2) > 128);
        Assert.Equal(255, maps.AlbedoHeight[4 * (size / 4 * size + size / 4) + 3]);
    }

    [Fact]
    public void HorizonVisibilityDarkensHollowsOnly()
    {
        const int size = 256;
        const float pixelMetres = MaterialMaps.TileMetres / size;
        var height = new TileImage(size);

        // A dish at (128, 128) and a dome at (64, 64), each 6 cm deep and 20 cm across:
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dish = Profile(x - 128, y - 128);
                var dome = Profile(x - 64, y - 64);
                height[x, y] = 0.06f * (dome - dish);
            }
        }

        var visibility = TileOps.HorizonVisibility(height, MaterialMaps.OcclusionRadiusMetres);

        Assert.True(visibility[128, 128] < 0.8f, $"{visibility[128, 128]} at the dish's bottom");
        Assert.Equal(1.0f, visibility[64, 64]);
        Assert.Equal(1.0f, visibility[200, 40]);

        static float Profile(int x, int y)
        {
            var distance = MathF.Sqrt(x * x + y * y) * pixelMetres;
            return MathF.Max(0.0f, 1.0f - distance * distance / 0.01f);
        }
    }

    [Fact]
    public void FlatMapsAreThePackedMapsOfUniformPlanes()
    {
        const int size = 16;
        var red = new TileImage(size);
        var green = new TileImage(size);
        var blue = new TileImage(size);
        var height = new TileImage(size);

        Array.Fill(red.Pixels, 0.47f);
        Array.Fill(green.Pixels, 0.11f);
        Array.Fill(blue.Pixels, 0.88f);
        Array.Fill(height.Pixels, 0.3f);

        var packed = MaterialMaps.Pack(red, green, blue, height);
        var flat = MaterialMaps.Flat(size, 0.47f, 0.11f, 0.88f);

        Assert.Equal(packed.AlbedoHeight, flat.AlbedoHeight);
        Assert.Equal(packed.Normal, flat.Normal);

        // A component c is stored as 128 + 127 c, so the shader decodes a flat normal exactly:
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, flat.Normal[..4]);
    }

    [Fact]
    public void LimestoneRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.LimestoneAt, 3);
    }

    [Fact]
    public void SnowRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.SnowAt, 4);
    }

    [Fact]
    public void IceRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.IceAt, 5);
    }

    [Fact]
    public void SandstoneRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.SandstoneAt, 6);
    }

    [Fact]
    public void ShaleRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.ShaleAt, 7);
    }

    [Fact]
    public void BasaltRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.BasaltAt, 8);
    }

    [Fact]
    public void GneissRepeatsEveryTile()
    {
        AssertRepeatsEveryTile(MaterialRecipes.GneissAt, 9);
    }

    [Fact]
    public void LimestoneMapsHaveNoSeam()
    {
        var maps = MaterialRecipes.Limestone(128);

        for (var channel = 0; channel < 4; channel++)
        {
            AssertSeamless(maps.AlbedoHeight, maps.Size, channel);
        }

        for (var channel = 0; channel < 3; channel++)
        {
            AssertSeamless(maps.Normal, maps.Size, channel);
        }
    }

    /// <summary>
    ///     Compares a surface function at random points with the same points shifted by whole tiles.
    /// </summary>
    private static void AssertRepeatsEveryTile(Func<float, float, SurfacePoint> surface, int seed)
    {
        var random = new Random(seed);

        for (var sample = 0; sample < 200; sample++)
        {
            var u = random.NextSingle();
            var v = random.NextSingle();
            var point = surface(u, v);
            var shifted = surface(u + 1.0f, v - 1.0f);

            Assert.Equal(point.Height, shifted.Height, 1e-4f);
            Assert.Equal(point.Red, shifted.Red, 1e-3f);
            Assert.Equal(point.Green, shifted.Green, 1e-3f);
            Assert.Equal(point.Blue, shifted.Blue, 1e-3f);
        }
    }

    private static byte NormalAt(MaterialMaps maps, int x, int y, int channel)
    {
        return maps.Normal[4 * (y * maps.Size + x) + channel];
    }

    /// <summary>
    ///     Compares the mean step across the wrapped edges with the mean step between neighbours inside the image.
    ///     A map that does not tile jumps at the edges and its seam steps are several times the inside ones.
    /// </summary>
    private static void AssertSeamless(byte[] texels, int size, int channel)
    {
        var inside = 0.0;
        var seam = 0.0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x + 1 < size; x++)
            {
                inside += Math.Abs(Texel(x, y) - Texel(x + 1, y)) + Math.Abs(Texel(y, x) - Texel(y, x + 1));
            }

            seam += Math.Abs(Texel(size - 1, y) - Texel(0, y)) + Math.Abs(Texel(y, size - 1) - Texel(y, 0));
        }

        inside /= 2.0 * size * (size - 1);
        seam /= 2.0 * size;

        Assert.True(seam < 1.5 * inside, $"Channel {channel}: a mean step of {seam:F2} across the seam against {inside:F2} inside");

        int Texel(int x, int y) => texels[4 * (y * size + x) + channel];
    }
}

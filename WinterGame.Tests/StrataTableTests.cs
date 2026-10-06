using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Generation.GraphProgram;

namespace WinterGame.Tests;

public class StrataTableTests
{
    private static StrataTable Cover()
    {
        return new StrataTable(
            TerrainStrata.BasementTopY,
            SurfaceMaterial.Gneiss,
            [8.0f, 7.0f, 6.0f, 2.0f, 9.0f, 24.0f],
            [
                SurfaceMaterial.Basalt,
                SurfaceMaterial.Limestone,
                SurfaceMaterial.Shale,
                SurfaceMaterial.Sandstone,
                SurfaceMaterial.Limestone,
                SurfaceMaterial.Sandstone
            ]
        );
    }

    private static (StrataTable Left, StrataTable Right) Pair()
    {
        var left = new StrataTable(
            -10.0f,
            SurfaceMaterial.Gneiss,
            [4.0f, 4.0f, 4.0f],
            [SurfaceMaterial.Sandstone, SurfaceMaterial.Shale, SurfaceMaterial.Basalt]
        );

        var right = new StrataTable(
            -10.0f,
            SurfaceMaterial.Gneiss,
            [4.0f, 4.0f, 4.0f],
            [SurfaceMaterial.Sandstone, SurfaceMaterial.Limestone, SurfaceMaterial.Basalt]
        );

        return (left, right);
    }

    [Fact]
    public void Select_HitsTheBandsInTableOrder()
    {
        var table = Cover();

        Assert.Equal(SurfaceMaterial.Basalt, table.Select(-29.0f));
        Assert.Equal(SurfaceMaterial.Limestone, table.Select(-21.0f));
        Assert.Equal(SurfaceMaterial.Shale, table.Select(-14.0f));
        Assert.Equal(SurfaceMaterial.Sandstone, table.Select(-8.0f));
        Assert.Equal(SurfaceMaterial.Limestone, table.Select(0.0f));
        Assert.Equal(SurfaceMaterial.Sandstone, table.Select(7.0f));
    }

    [Fact]
    public void Select_ClampsAtBothEnds()
    {
        var table = Cover();

        // Below the basement top is the shared basement, and everything above the last band is the youngest one:
        Assert.Equal(SurfaceMaterial.Gneiss, table.Select(-500.0f));
        Assert.Equal(SurfaceMaterial.Sandstone, table.Select(1000.0f));

        Assert.Equal(6, table.BandCount);
        Assert.Equal(26.0f, table.BandTop(table.BandCount - 1));
    }

    [Fact]
    public void Select_RejectsAThicknessThatIsNotPositive()
    {
        Assert.Throws<ArgumentException>(
            () => new StrataTable(0.0f, SurfaceMaterial.Gneiss, [4.0f, 0.0f], [SurfaceMaterial.Shale, SurfaceMaterial.Basalt])
        );

        Assert.Throws<ArgumentException>(
            () => new StrataTable(0.0f, SurfaceMaterial.Gneiss, [4.0f], [SurfaceMaterial.Shale, SurfaceMaterial.Basalt])
        );
    }

    [Fact]
    public void BandedTable_FollowsTheTableUpAColumn()
    {
        var table = Cover();
        var builder = new DensityGraphBuilder(1);
        var bands = builder.BandedTable(builder.Y, builder.Constant(0.0f), new StrataSet(0.0f, table));
        var sampler = new DensitySampler(builder.Compile(bands));

        var spacing = DensitySampler.SampleSpacing(0);
        var heights = new[] { -29.0f, -21.0f, -14.0f, -8.0f, 0.0f, 7.0f };
        var expected = new[]
        {
            SurfaceMaterial.Basalt,
            SurfaceMaterial.Limestone,
            SurfaceMaterial.Shale,
            SurfaceMaterial.Sandstone,
            SurfaceMaterial.Limestone,
            SurfaceMaterial.Sandstone
        };

        for (var index = 0; index < heights.Length; index++)
        {
            var y = (int)MathF.Round(heights[index] / spacing);

            Assert.Equal(expected[index], (byte)sampler.Sample(new Vector3i(0, y, 0), 0));
        }
    }

    [Fact]
    public void Transition_AtTheEndpointsIsExactlyEachTable()
    {
        var (left, right) = Pair();
        var set = new StrataSet(0.25f, left, right);

        for (var step = -12; step <= 4; step++)
        {
            var y = step + 0.25f;

            Assert.Equal(left.Select(y), set.Select(0.0f, y));
            Assert.Equal(right.Select(y), set.Select(2.0f, y));
        }
    }

    [Fact]
    public void Transition_HidesAMismatchedBedAtTheMiddle()
    {
        var (left, right) = Pair();
        var set = new StrataSet(0.25f, left, right);

        var sawLeftBed = false;
        var sawRightBed = false;

        for (var step = -12; step <= 4; step++)
        {
            var y = step + 0.25f;
            var middle = set.Select(1.0f, y);

            sawLeftBed |= middle == SurfaceMaterial.Shale;
            sawRightBed |= middle == SurfaceMaterial.Limestone;

            Assert.NotEqual(SurfaceMaterial.Shale, middle);
            Assert.NotEqual(SurfaceMaterial.Limestone, middle);
        }

        Assert.False(sawLeftBed, "The left province's own bed survived the middle of its transition.");
        Assert.False(sawRightBed, "The right province's own bed survived the middle of its transition.");
    }

    [Fact]
    public void Transition_KeepsAWedgedBedPresentOnItsOwnSideOfTheMiddle()
    {
        var (left, right) = Pair();
        var set = new StrataSet(0.25f, left, right);

        // A fifth of the way into the band the left bed is still present and thinner, and the right bed is still absent:
        var beforeMiddle = new[] { -5.5f, -5.0f, -4.5f, -4.0f };
        var afterMiddle = new[] { -5.5f, -5.0f, -4.5f, -4.0f };

        Assert.Contains(SurfaceMaterial.Shale, Sample(beforeMiddle, y => set.Select(0.9f, y)));
        Assert.DoesNotContain(SurfaceMaterial.Limestone, Sample(beforeMiddle, y => set.Select(0.9f, y)));
        Assert.Contains(SurfaceMaterial.Limestone, Sample(afterMiddle, y => set.Select(1.1f, y)));
        Assert.DoesNotContain(SurfaceMaterial.Shale, Sample(afterMiddle, y => set.Select(1.1f, y)));
    }

    [Fact]
    public void Transition_OnlyReturnsIdsTheTablesHold()
    {
        var (left, right) = Pair();
        var set = new StrataSet(0.25f, left, right);
        var allowed = new HashSet<byte> { left.BelowId, right.BelowId };

        for (var band = 0; band < left.BandCount; band++)
        {
            allowed.Add(left.BandId(band));
            allowed.Add(right.BandId(band));
        }

        for (var coordinateStep = 0; coordinateStep <= 40; coordinateStep++)
        {
            var coordinate = coordinateStep * 0.05f;

            for (var heightStep = -13; heightStep <= 5; heightStep++)
            {
                Assert.Contains(set.Select(coordinate, heightStep + 0.5f), allowed);
            }
        }
    }

    [Fact]
    public void Transition_KeepsTheBasementContactFlat()
    {
        var (left, right) = Pair();
        var set = new StrataSet(0.25f, left, right);

        for (var coordinateStep = 0; coordinateStep <= 40; coordinateStep++)
        {
            Assert.Equal(SurfaceMaterial.Gneiss, set.Select(coordinateStep * 0.05f, -10.5f));
            Assert.Equal(SurfaceMaterial.Gneiss, set.Select(coordinateStep * 0.05f, -30.0f));
        }
    }

    [Fact]
    public void Transition_WithAZeroHalfWidthIsAHardFault()
    {
        var (left, right) = Pair();
        var set = new StrataSet(0.0f, left, right);

        Assert.Equal(left.Select(-4.0f), set.Select(0.99f, -4.0f));
        Assert.Equal(right.Select(-4.0f), set.Select(1.01f, -4.0f));
        Assert.Equal(SurfaceMaterial.Shale, set.Select(0.99f, -4.0f));
        Assert.Equal(SurfaceMaterial.Limestone, set.Select(1.01f, -4.0f));
    }

    [Fact]
    public void StrataSet_RejectsMixedBasementTops()
    {
        var (left, _) = Pair();

        var other = new StrataTable(
            -12.0f,
            SurfaceMaterial.Gneiss,
            [4.0f, 4.0f, 4.0f],
            [SurfaceMaterial.Sandstone, SurfaceMaterial.Limestone, SurfaceMaterial.Basalt]
        );

        Assert.Throws<ArgumentException>(() => new StrataSet(0.25f, left, other));
    }

    [Fact]
    public void StrataSet_RejectsAHalfWidthOutsideTheRange()
    {
        var (left, right) = Pair();

        Assert.Throws<ArgumentOutOfRangeException>(() => new StrataSet(-0.1f, left, right));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrataSet(0.51f, left, right));
    }

    private static byte[] Sample(float[] heights, Func<float, byte> select)
    {
        var result = new byte[heights.Length];

        for (var index = 0; index < heights.Length; index++)
        {
            result[index] = select(heights[index]);
        }

        return result;
    }
}

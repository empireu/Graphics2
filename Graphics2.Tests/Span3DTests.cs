using Graphics2.Data;

namespace Graphics2.Tests;

public class Span3DTests
{
    [Fact]
    public void Indexer_UsesRowMajorLayout()
    {
        var data = new byte[24];
       
        // ReSharper disable once UseObjectOrCollectionInitializer
        var box = new Span3D<byte>(data, 4, 3, 2);

        box[1, 2, 1] = 7;
        box[3, 0, 0] = 9;
        box[0, 0, 1] = 5;

        Assert.Equal((byte)7, data[1 + 2 * 4 + 1 * 4 * 3]);
        Assert.Equal((byte)9, data[3]);
        Assert.Equal((byte)5, data[4 * 3]);
    }

    [Fact]
    public void Indexer_ReturnsAReferenceIntoTheSpan()
    {
        var data = new byte[24];
        var box = new Span3D<byte>(data, 4, 3, 2);

        ref var slot = ref box[2, 1, 1];
        slot = 42;

        Assert.Equal((byte)42, data[2 + 1 * 4 + 1 * 4 * 3]);
    }

    [Fact]
    public void Data_ExposesOnlyTheBox()
    {
        var data = new byte[64];
        var box = new Span3D<byte>(data.AsSpan(8, 24), 4, 3, 2);

        Assert.Equal(24, box.Length);
        Assert.Equal(24, box.Data.Length);

        box[0, 0, 0] = 3;

        Assert.Equal((byte)3, data[8]);
        Assert.Equal((byte)0, data[7]);
        Assert.Equal((byte)0, data[32]);
    }

    [Fact]
    public void Constructor_RejectsASpanOfTheWrongLength()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = new Span3D<byte>(new byte[10], 4, 3, 2);
        });
    }

    [Theory]
    [InlineData(0, 3, 2)]
    [InlineData(4, 0, 2)]
    [InlineData(4, 3, 0)]
    [InlineData(-1, 3, 2)]
    public void Constructor_RejectsNonPositiveDimensions(int width, int height, int depth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new Span3D<byte>(new byte[24], width, height, depth);
        });
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(4, 0, 0)]
    [InlineData(0, 3, 0)]
    [InlineData(0, 0, 2)]
    public void GetChecked_ThrowsOutsideTheBox(int x, int y, int z)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var box = new Span3D<byte>(new byte[24], 4, 3, 2);
            box.GetChecked(x, y, z);
        });
    }

    [Fact]
    public void GetChecked_ReturnsTheSameSlotAsTheIndexer()
    {
        var data = new byte[24];
        var box = new Span3D<byte>(data, 4, 3, 2);

        box.GetChecked(1, 2, 1) = 7;

        Assert.Equal((byte)7, box[1, 2, 1]);
    }
}

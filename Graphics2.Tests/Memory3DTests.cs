using Graphics2.Data;

namespace Graphics2.Tests;

public class Memory3DTests
{
    [Fact]
    public void Allocate_CreatesAZeroedBox()
    {
        var box = Memory3D<byte>.Allocate(4, 3, 2);

        Assert.False(box.IsEmpty);
        Assert.Equal(4, box.SizeX);
        Assert.Equal(3, box.SizeY);
        Assert.Equal(2, box.SizeZ);
        Assert.Equal(24, box.Length);
        Assert.Equal(24, box.Span.Length);
        Assert.Equal(new byte[24], box.Span.Data.ToArray());
    }

    [Fact]
    public void Constructor_RespectsTheOffset()
    {
        var data = new byte[64];
        var box = new Memory3D<byte>(data, 8, 4, 3, 2);

        box.Span[0, 0, 0] = 1;
        box.Span[3, 2, 1] = 2;

        Assert.Equal((byte)1, data[8]);
        Assert.Equal((byte)2, data[8 + 3 + 2 * 4 + 1 * 4 * 3]);
        Assert.Equal((byte)0, data[7]);
        Assert.Equal((byte)0, data[8 + 24]);
    }

    [Fact]
    public void Span_IsBackedByTheUnderlyingArray()
    {
        var data = new byte[24];
        var box = new Memory3D<byte>(data, 4, 3, 2);

        box.Span[1, 2, 1] = 7;

        Assert.Equal((byte)7, data[1 + 2 * 4 + 1 * 4 * 3]);
        Assert.Equal((byte)7, box.Span[1, 2, 1]);
    }

    [Fact]
    public void Constructor_RejectsABoxThatDoesNotFit()
    {
        Assert.Throws<ArgumentException>(() => new Memory3D<byte>(new byte[23], 4, 3, 2));
        Assert.Throws<ArgumentException>(() => new Memory3D<byte>(new byte[64], 41, 4, 3, 2));
        Assert.Throws<ArgumentNullException>(() => new Memory3D<byte>(null!, 4, 3, 2));
    }

    [Fact]
    public void Default_IsEmpty()
    {
        var box = default(Memory3D<byte>);

        Assert.True(box.IsEmpty);
        Assert.Equal(0, box.Length);
        Assert.True(box.Span.IsEmpty);
        Assert.Equal(0, box.Span.Length);
    }
}

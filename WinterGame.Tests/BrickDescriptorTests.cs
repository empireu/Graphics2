using System.Runtime.InteropServices;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class BrickDescriptorTests
{
    [Fact]
    public void Descriptor_IsOneThirtyTwoBitWord()
    {
        Assert.Equal(sizeof(uint), Marshal.SizeOf<BrickDescriptor>());
        Assert.Equal(30, BrickDescriptor.BrickUserDataBits);
        Assert.Equal(0x3FFFFFFFu, BrickDescriptor.MaxUserData);
    }

    [Theory]
    [InlineData(BrickType.Provider, 0)]
    [InlineData(BrickType.Uniform, 0)]
    [InlineData(BrickType.Uniform, 255)]
    [InlineData(BrickType.Dense, 0)]
    [InlineData(BrickType.Dense, 123456)]
    public void Pack_RoundTripsThroughTheRawWord(BrickType type, int userData)
    {
        var descriptor = BrickDescriptor.Pack(type, (uint)userData);
        var rebuilt = BrickDescriptor.FromPacked(descriptor.ToPacked());

        Assert.Equal(type, descriptor.Type);
        Assert.Equal((uint)userData, descriptor.UserData);
        Assert.Equal(descriptor, rebuilt);
    }

    [Fact]
    public void Pack_RoundTripsTheWidestUserData()
    {
        var descriptor = BrickDescriptor.Pack(BrickType.Dense, BrickDescriptor.MaxUserData);

        Assert.Equal(BrickDescriptor.MaxUserData, descriptor.UserData);
        Assert.Equal(BrickDescriptor.MaxUserData, BrickDescriptor.FromPacked(descriptor.ToPacked()).UserData);
    }

    [Fact]
    public void Pack_PutsTheTypeInTheLowTwoBits()
    {
        var descriptor = BrickDescriptor.Pack(BrickType.Dense, 0x2AAAAAAAu);

        Assert.Equal(0b11u, descriptor.ToPacked() & 0b11u);
        Assert.Equal((0x2AAAAAAAu << 2) | 0b11u, descriptor.ToPacked());
    }

    [Fact]
    public void DefaultDescriptor_IsNone()
    {
        Assert.Equal(BrickType.None, default(BrickDescriptor).Type);
    }

    [Fact]
    public void Equality_IsByPackedWord()
    {
        var first = BrickDescriptor.Pack(BrickType.Uniform, 42);
        var same = BrickDescriptor.Pack(BrickType.Uniform, 42);
        var different = BrickDescriptor.Pack(BrickType.Uniform, 43);

        Assert.True(first == same);
        Assert.False(first != same);
        Assert.True(first != different);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }
}

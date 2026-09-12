using Graphics2.Data;
using Graphics2.Mathematics;
using Graphics2.Voxels;

namespace Graphics2.Tests;

public class DenseVoxelStorageTests
{
    private static (DenseVoxelStorage Storage, Memory3D<byte> Content, Memory3D<byte> Material) CreateStorage(int sizeX, int sizeY, int sizeZ)
    {
        var content = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);
        var material = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);

        return (new DenseVoxelStorage(content, material), content, material);
    }

    [Fact]
    public void SizeInVoxels_MatchesTheAllocatedBox()
    {
        var (storage, _, _) = CreateStorage(8, 4, 2);

        Assert.Equal(new Vector3i(8, 4, 2), storage.SizeInVoxels);
    }

    [Fact]
    public void Constructor_RejectsMismatchedChannelSizes()
    {
        var content = Memory3D<byte>.Allocate(8, 8, 8);
        var material = Memory3D<byte>.Allocate(8, 8, 4);

        Assert.Throws<ArgumentException>(() => new DenseVoxelStorage(content, material));
    }

    [Fact]
    public void ReadRange_CopiesTheBox()
    {
        var (storage, content, material) = CreateStorage(8, 4, 2);
        var sourceContent = content.Span;
        var sourceMaterial = material.Span;

        for (var z = 0; z < 2; z++)
        {
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    sourceContent[x, y, z] = (byte)(x + 1);
                    sourceMaterial[x, y, z] = (byte)(y + 1);
                }
            }
        }

        var destinationContent = Memory3D<byte>.Allocate(3, 2, 2);
        var destinationMaterial = Memory3D<byte>.Allocate(3, 2, 2);

        storage.ReadRange(0, new Vector3i(2, 1, 0), new Vector3i(4, 2, 1), destinationContent.Span, destinationMaterial.Span);

        var readContent = destinationContent.Span;
        var readMaterial = destinationMaterial.Span;

        Assert.Equal((byte)3, readContent[0, 0, 0]);
        Assert.Equal((byte)3, readContent[0, 0, 1]);
        Assert.Equal((byte)5, readContent[2, 1, 1]);
        Assert.Equal((byte)2, readMaterial[0, 0, 0]);
        Assert.Equal((byte)3, readMaterial[0, 1, 0]);
        Assert.Equal((byte)3, readMaterial[2, 1, 1]);
    }

    [Fact]
    public void ReadRange_ReadsEverySecondVoxelAtLodOne()
    {
        var (storage, content, material) = CreateStorage(8, 2, 2);
        var sourceContent = content.Span;
        var sourceMaterial = material.Span;

        for (var x = 0; x < 8; x++)
        {
            sourceContent[x, 0, 0] = (byte)x;
            sourceMaterial[x, 0, 0] = (byte)(100 + x);
        }

        var destinationContent = Memory3D<byte>.Allocate(2, 1, 1);
        var destinationMaterial = Memory3D<byte>.Allocate(2, 1, 1);

        storage.ReadRange(1, new Vector3i(2, 0, 0), new Vector3i(3, 0, 0), destinationContent.Span, destinationMaterial.Span);

        var readContent = destinationContent.Span;
        var readMaterial = destinationMaterial.Span;

        Assert.Equal((byte)4, readContent[0, 0, 0]);
        Assert.Equal((byte)6, readContent[1, 0, 0]);
        Assert.Equal((byte)104, readMaterial[0, 0, 0]);
        Assert.Equal((byte)106, readMaterial[1, 0, 0]);
    }

    [Fact]
    public void ReadRange_RejectsADestinationOfTheWrongSize()
    {
        var (storage, _, _) = CreateStorage(8, 8, 8);

        var right = Memory3D<byte>.Allocate(3, 3, 3);
        var wrong = Memory3D<byte>.Allocate(2, 2, 2);

        Assert.Throws<ArgumentException>(() =>
            storage.ReadRange(0, new Vector3i(0, 0, 0), new Vector3i(2, 2, 2), wrong.Span, right.Span));

        Assert.Throws<ArgumentException>(() =>
            storage.ReadRange(0, new Vector3i(0, 0, 0), new Vector3i(2, 2, 2), right.Span, wrong.Span));
    }

    [Fact]
    public void ReadRange_RejectsABoxOutsideTheStorage()
    {
        var (storage, _, _) = CreateStorage(8, 8, 8);

        var destination = Memory3D<byte>.Allocate(9, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.ReadRange(0, new Vector3i(-1, 0, 0), new Vector3i(7, 0, 0), destination.Span, destination.Span));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.ReadRange(0, new Vector3i(0, 0, 0), new Vector3i(8, 0, 0), destination.Span, destination.Span));
    }

    [Fact]
    public void ReadRange_RejectsABoxReachingPastTheStorageAtACoarserLod()
    {
        var (storage, _, _) = CreateStorage(8, 8, 8);

        var destination = Memory3D<byte>.Allocate(5, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.ReadRange(1, new Vector3i(0, 0, 0), new Vector3i(4, 0, 0), destination.Span, destination.Span));
    }

    [Fact]
    public void ReadRange_RejectsANegativeLod()
    {
        var (storage, _, _) = CreateStorage(8, 8, 8);

        var destination = Memory3D<byte>.Allocate(1, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            storage.ReadRange(-1, new Vector3i(0, 0, 0), new Vector3i(0, 0, 0), destination.Span, destination.Span));
    }
}

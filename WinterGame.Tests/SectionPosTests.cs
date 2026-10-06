using WinterGame.World;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SectionPosTests
{
    [Theory]
    [InlineData(12345, -6789, 101112)]
    [InlineData(SectionPos.MaxX, SectionPos.MaxY, SectionPos.MaxZ)]
    [InlineData(SectionPos.MinX, SectionPos.MinY, SectionPos.MinZ)]
    public void Pack_AndUnpack_RoundTrip(int x, int y, int z)
    {
        var key = SectionPos.Pack(x, y, z);

        SectionPos.Unpack(key, out var unpackedX, out var unpackedY, out var unpackedZ);

        Assert.Equal(x, unpackedX);
        Assert.Equal(y, unpackedY);
        Assert.Equal(z, unpackedZ);
    }

    [Fact]
    public void Pack_AndUnpack_RoundTripEveryCoordinateOfASmallCube()
    {
        for (var z = -3; z <= 3; z++)
        {
            for (var y = -3; y <= 3; y++)
            {
                for (var x = -3; x <= 3; x++)
                {
                    SectionPos.Unpack(SectionPos.Pack(x, y, z), out var unpackedX, out var unpackedY, out var unpackedZ);

                    Assert.Equal(x, unpackedX);
                    Assert.Equal(y, unpackedY);
                    Assert.Equal(z, unpackedZ);
                }
            }
        }
    }

    [Fact]
    public void SectionSize_IsTheVoxelCountPerAxis()
    {
        Assert.Equal(1 << SectionPos.Log, SectionPos.SectionSize);
        Assert.Equal(SectionPos.SectionSize - 1, SectionPos.SectionMask);
    }

    [Fact]
    public void GetKey_MatchesThePackedCoordinate()
    {
        var position = new SectionPos(-7, 3, 9);

        Assert.Equal(SectionPos.Pack(position.X, position.Y, position.Z), position.GetKey());
    }

    [Fact]
    public void GetOrigin_IsTheFirstVoxelOfTheSection()
    {
        var position = new SectionPos(-7, 3, 9);
        var origin = position.GetOrigin();

        Assert.Equal(-7 * SectionPos.SectionSize, origin.X);
        Assert.Equal(3 * SectionPos.SectionSize, origin.Y);
        Assert.Equal(9 * SectionPos.SectionSize, origin.Z);
    }

    [Fact]
    public void GetOrigin_AndTheSectionMask_MapEveryVoxelBackToTheSection()
    {
        var position = new SectionPos(-2, 5, -3);
        var origin = position.GetOrigin();

        Assert.Equal(position.X, SectionPos.VoxelToSectionCoord(origin.X));
        Assert.Equal(position.Y, SectionPos.VoxelToSectionCoord(origin.Y));
        Assert.Equal(position.Z, SectionPos.VoxelToSectionCoord(origin.Z));

        Assert.Equal(position.X, SectionPos.VoxelToSectionCoord(origin.X + SectionPos.SectionMask));
        Assert.Equal(position.Y, SectionPos.VoxelToSectionCoord(origin.Y + SectionPos.SectionMask));
        Assert.Equal(position.Z, SectionPos.VoxelToSectionCoord(origin.Z + SectionPos.SectionMask));
    }

    [Fact]
    public void Constructor_FromAKey_UnpacksToTheSameCoordinate()
    {
        var position = new SectionPos(-7, 3, 9);
        var restored = new SectionPos(SectionPos.Pack(position.X, position.Y, position.Z));

        Assert.Equal(position.X, restored.X);
        Assert.Equal(position.Y, restored.Y);
        Assert.Equal(position.Z, restored.Z);
    }

    [Fact]
    public void VoxelToSectionCoord_RoundsTowardNegativeInfinity()
    {
        Assert.Equal(0, SectionPos.VoxelToSectionCoord(0));
        Assert.Equal(0, SectionPos.VoxelToSectionCoord(31));
        Assert.Equal(1, SectionPos.VoxelToSectionCoord(32));
        Assert.Equal(-1, SectionPos.VoxelToSectionCoord(-1));
        Assert.Equal(-1, SectionPos.VoxelToSectionCoord(-32));
        Assert.Equal(-2, SectionPos.VoxelToSectionCoord(-33));
    }

    [Fact]
    public void SectionToVoxelCoord_IsTheFirstVoxelOfTheSection()
    {
        for (var coord = -4; coord <= 4; coord++)
        {
            var voxel = SectionPos.SectionToVoxelCoord(coord);

            Assert.Equal(0, voxel & ((1 << SectionPos.Log) - 1));
            Assert.Equal(coord, SectionPos.VoxelToSectionCoord(voxel));
            Assert.Equal(coord, SectionPos.VoxelToSectionCoord(voxel + (1 << SectionPos.Log) - 1));
        }
    }
}

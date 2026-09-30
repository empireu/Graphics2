using WinterGame.World;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SectionArenaTests
{
    private static readonly SectionPos Origin = new(0, 0, 0);

    [Fact]
    public void AllocateDescriptorArray_ReturnsAWholeSectionSpan()
    {
        var arena = new SectionArena();

        Assert.Equal(Section.DescriptorsPerSection, arena.AllocateDescriptorArray().Span.Length);
    }

    [Fact]
    public void AllocateDescriptorArray_ReturnsDistinctNonOverlappingArrays()
    {
        var arena = new SectionArena();

        var first = arena.AllocateDescriptorArray();
        var second = arena.AllocateDescriptorArray();

        Assert.NotEqual(first, second);
        Assert.False(first.Span.Overlaps(second.Span));
    }

    [Fact]
    public void FreeDescriptorArray_MakesTheArrayAvailableAgain()
    {
        var arena = new SectionArena();
        var first = arena.AllocateDescriptorArray();

        arena.FreeDescriptorArray(first);

        Assert.Equal(first, arena.AllocateDescriptorArray());
    }

    [Fact]
    public void AllocateDescriptorArray_StartsEveryArrayOnASectionBoundaryAndFillsTheWholePage()
    {
        var arena = new SectionArena();

        for (var index = 0; index < SectionArena.DefaultDescriptorArraysPerPage; index++)
        {
            var descriptorArray = arena.AllocateDescriptorArray();

            Assert.Equal(0, descriptorArray.Address % Section.DescriptorsPerSection);
            Assert.Equal(Section.DescriptorsPerSection, descriptorArray.Span.Length);
        }

        Assert.Equal(1, arena.DescriptorPageCount);
    }

    [Fact]
    public void AllocateVoxelArray_ReturnsAWholeBrickSpan()
    {
        var arena = new SectionArena();

        Assert.Equal(Section.VoxelsPerBrick, arena.AllocateVoxelArray().Span.Length);
    }

    [Fact]
    public void AllocateVoxelArray_ReturnsDistinctNonOverlappingArrays()
    {
        var arena = new SectionArena();

        var first = arena.AllocateVoxelArray();
        var second = arena.AllocateVoxelArray();

        Assert.NotEqual(first, second);
        Assert.False(first.Span.Overlaps(second.Span));
    }

    [Fact]
    public void GetVoxelArray_ResolvesTheHandleBackToTheSameMemory()
    {
        var arena = new SectionArena();
        var voxelArray = arena.AllocateVoxelArray();

        voxelArray.Span[7] = 42;

        var resolved = arena.GetVoxelArray(voxelArray.Handle);

        Assert.Equal(voxelArray, resolved);
        Assert.Equal(42, resolved.Span[7]);
    }

    [Fact]
    public void FreeVoxelArray_MakesTheArrayAvailableAgain()
    {
        var arena = new SectionArena();
        var first = arena.AllocateVoxelArray();

        arena.FreeVoxelArray(first);

        Assert.Equal(first, arena.AllocateVoxelArray());
    }

    [Fact]
    public void Create_FillsEveryDescriptorWithNone()
    {
        var section = new SectionArena().Create(Origin);

        Assert.True(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.None));
        Assert.True(section.AreAllBricksOfType(VoxelChannel.Material, BrickType.None));
    }

    [Fact]
    public void Create_RejectsADuplicatePosition()
    {
        var arena = new SectionArena();
        arena.Create(Origin);

        Assert.Throws<InvalidOperationException>(() => arena.Create(Origin));
    }

    [Fact]
    public void Destroy_RejectsAnUnknownSection()
    {
        var arena = new SectionArena();

        Assert.Throws<InvalidOperationException>(() => arena.Destroy(Origin));
    }

    [Fact]
    public void Destroy_ReleasesTheDescriptorArray()
    {
        var arena = new SectionArena();
        var section = arena.Create(Origin);
        var descriptorArray = section.DescriptorArray;

        arena.Destroy(Origin);

        Assert.Equal(descriptorArray, arena.AllocateDescriptorArray());
    }

    [Fact]
    public void Destroy_ReleasesEveryDenseArray()
    {
        var arena = new SectionArena();
        var section = arena.Create(Origin);

        section.SetDense(VoxelChannel.Content, 5);
        section.SetDense(VoxelChannel.Material, 9);

        var content = section.GetDense(VoxelChannel.Content, 5);
        var material = section.GetDense(VoxelChannel.Material, 9);

        arena.Destroy(Origin);

        var reused = new[] { arena.AllocateVoxelArray(), arena.AllocateVoxelArray() };

        Assert.Contains(content, reused);
        Assert.Contains(material, reused);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, SectionArena.MaxVoxelArraysPerPage + 1)]
    public void Constructor_RejectsAnInvalidPageSize(int descriptorArraysPerPage, int voxelArraysPerPage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SectionArena(descriptorArraysPerPage, voxelArraysPerPage));
    }

    [Fact]
    public void MaxVoxelArraysPerPage_KeepsTheLastAddressPackable()
    {
        var lastAddress = (SectionArena.MaxVoxelArraysPerPage - 1) * Section.VoxelsPerBrick;

        Assert.True(lastAddress <= SectionArena.MaxAddressNumber);
        Assert.True(lastAddress + Section.VoxelsPerBrick > SectionArena.MaxAddressNumber);
    }

    [Fact]
    public void SmallPages_GrowByOnePagePerPageOfArrays()
    {
        var arena = new SectionArena(descriptorArraysPerPage: 1, voxelArraysPerPage: 2);

        var descriptorArrays = new[] { arena.AllocateDescriptorArray(), arena.AllocateDescriptorArray() };
        var voxelArrays = new[] { arena.AllocateVoxelArray(), arena.AllocateVoxelArray(), arena.AllocateVoxelArray() };

        Assert.Equal(2, arena.DescriptorPageCount);
        Assert.Equal(2, arena.VoxelPageCount);
        Assert.False(descriptorArrays[0].Span.Overlaps(descriptorArrays[1].Span));
        Assert.Equal(voxelArrays.Length, voxelArrays.Distinct().Count());

        foreach (var voxelArray in voxelArrays)
        {
            Assert.Equal(voxelArray, arena.GetVoxelArray(voxelArray.Handle));
        }
    }

    [Fact]
    public void SectionSizedArena_HoldsAFullyDenseSectionWithoutGrowing()
    {
        var arena = CreateSectionSizedArena();

        for (var round = 0; round < 3; round++)
        {
            var section = arena.Create(Origin);

            FillDense(section);

            arena.Destroy(Origin);
        }

        Assert.Equal(1, arena.DescriptorPageCount);
        Assert.Equal(1, arena.VoxelPageCount);
    }

    [Fact]
    public void CreateCopy_CopiesUniformAndDenseBricksAtTheSamePosition()
    {
        var position = new SectionPos(3, -2, 7);
        var source = CreateSectionSizedArena().Create(position);

        FillPattern(source);

        var copy = new SectionArena().CreateCopy(source);

        Assert.Equal(position, copy.Position);
        AssertMatchesPattern(copy);
    }

    [Fact]
    public void CreateCopy_DoesNotAliasTheSourceArena()
    {
        var sourceArena = CreateSectionSizedArena();
        var source = sourceArena.Create(Origin);

        FillPattern(source);

        var copy = new SectionArena().CreateCopy(source);

        FillDense(source);
        source.GetDense(VoxelChannel.Content, 1).Span.Fill(0xEE);
        sourceArena.Destroy(Origin);

        AssertMatchesPattern(copy);
    }

    [Fact]
    public void CreateCopy_LeavesTheSourceUnchanged()
    {
        var source = CreateSectionSizedArena().Create(Origin);

        FillPattern(source);

        new SectionArena().CreateCopy(source);

        AssertMatchesPattern(source);
    }

    [Fact]
    public void CreateCopy_RejectsAnUnwrittenBrickAndLeavesNoSectionBehind()
    {
        var source = CreateSectionSizedArena().Create(Origin);

        for (var index = 0; index < Section.Bricks; index++)
        {
            source.SetDense(VoxelChannel.Content, index);
        }

        var destination = new SectionArena();
        var descriptorArray = destination.AllocateDescriptorArray();
        destination.FreeDescriptorArray(descriptorArray);

        Assert.Throws<InvalidOperationException>(() => destination.CreateCopy(source));

        Assert.Throws<InvalidOperationException>(() => destination.Destroy(Origin));
        Assert.Equal(descriptorArray, destination.AllocateDescriptorArray());
    }

    [Fact]
    public void CreateCopy_RejectsAPositionTheDestinationAlreadyHolds()
    {
        var source = CreateSectionSizedArena().Create(Origin);

        FillPattern(source);

        var destination = new SectionArena();
        destination.Create(Origin);

        Assert.Throws<InvalidOperationException>(() => destination.CreateCopy(source));
    }

    private static SectionArena CreateSectionSizedArena()
    {
        return new SectionArena(descriptorArraysPerPage: 1, voxelArraysPerPage: Section.DescriptorsPerSection);
    }

    private static void FillDense(Section section)
    {
        foreach (var channel in Enum.GetValues<VoxelChannel>())
        {
            for (var index = 0; index < Section.Bricks; index++)
            {
                section.SetDense(channel, index);
            }
        }
    }

    /// <summary>
    ///     Odd bricks are dense with values derived from the channel, brick and voxel, even bricks are uniform with a value derived from the brick.
    /// </summary>
    private static void FillPattern(Section section)
    {
        foreach (var channel in Enum.GetValues<VoxelChannel>())
        {
            for (var index = 0; index < Section.Bricks; index++)
            {
                if (index % 2 == 0)
                {
                    section.SetUniform(channel, index, UniformPatternValue(channel, index));
                    continue;
                }

                section.SetDense(channel, index);

                var span = section.GetDense(channel, index).Span;

                for (var voxel = 0; voxel < Section.VoxelsPerBrick; voxel++)
                {
                    span[voxel] = DensePatternValue(channel, index, voxel);
                }
            }
        }
    }

    private static void AssertMatchesPattern(Section section)
    {
        foreach (var channel in Enum.GetValues<VoxelChannel>())
        {
            for (var index = 0; index < Section.Bricks; index++)
            {
                if (index % 2 == 0)
                {
                    Assert.Equal(BrickType.Uniform, section.GetBrickType(channel, index));
                    Assert.Equal(UniformPatternValue(channel, index), section.GetUniformValue(channel, index));
                    continue;
                }

                Assert.Equal(BrickType.Dense, section.GetBrickType(channel, index));

                var span = section.GetDense(channel, index).Span;

                for (var voxel = 0; voxel < Section.VoxelsPerBrick; voxel++)
                {
                    Assert.Equal(DensePatternValue(channel, index, voxel), span[voxel]);
                }
            }
        }
    }

    private static byte UniformPatternValue(VoxelChannel channel, int brickIndex)
    {
        return (byte)(brickIndex * 3 + (int)channel);
    }

    private static byte DensePatternValue(VoxelChannel channel, int brickIndex, int voxel)
    {
        return (byte)(brickIndex * 7 + voxel * 13 + (int)channel * 101);
    }
}

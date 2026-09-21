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
}

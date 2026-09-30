using WinterGame.World;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SectionTests
{
    private static readonly SectionPos Origin = new(0, 0, 0);

    [Fact]
    public void Bricks_CoverTheSectionAndEveryChannelHasADescriptor()
    {
        Assert.Equal(2, Section.BrickLog);
        Assert.Equal(SectionPos.SectionSize / Section.BrickSize, Section.BricksPerAxis);
        Assert.Equal(Section.BricksPerAxis * Section.BricksPerAxis * Section.BricksPerAxis, Section.Bricks);
        Assert.Equal(512, Section.Bricks);
        Assert.Equal(64, Section.VoxelsPerBrick);
        Assert.Equal(2, Section.VoxelChannelCount);
        Assert.Equal(Section.Bricks, Section.DescriptorsPerChannel);
        Assert.Equal(Section.Bricks * Section.VoxelChannelCount, Section.DescriptorsPerSection);
    }

    [Theory]
    [InlineData(VoxelChannel.Content)]
    [InlineData(VoxelChannel.Material)]
    public void Create_StartsWithEveryBrickNone(VoxelChannel channel)
    {
        var section = CreateSection();

        Assert.True(section.AreAllBricksOfType(channel, BrickType.None));
    }

    [Fact]
    public void SetUniform_SetDense_AreIndependentPerBrick()
    {
        var section = CreateSection();

        section.SetUniform(VoxelChannel.Content, 17, 201);
        section.SetDense(VoxelChannel.Material, 17);

        Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, 17));
        Assert.Equal(201, section.GetUniformValue(VoxelChannel.Content, 17));

        Assert.Equal(BrickType.Dense, section.GetBrickType(VoxelChannel.Material, 17));
        Assert.Equal(Section.VoxelsPerBrick, section.GetDense(VoxelChannel.Material, 17).Span.Length);
    }

    [Theory]
    [InlineData(VoxelChannel.Content)]
    [InlineData(VoxelChannel.Material)]
    public void SetUniform_RoundTripsAtEveryIndex(VoxelChannel channel)
    {
        var section = CreateSection();

        for (var index = 0; index < Section.Bricks; index++)
        {
            section.SetUniform(channel, index, (byte)index);
        }

        for (var index = 0; index < Section.Bricks; index++)
        {
            Assert.Equal(BrickType.Uniform, section.GetBrickType(channel, index));
            Assert.Equal((byte)index, section.GetUniformValue(channel, index));
        }
    }

    [Theory]
    [InlineData(VoxelChannel.Content)]
    [InlineData(VoxelChannel.Material)]
    public void SetDense_RoundTripsAtEveryIndex(VoxelChannel channel)
    {
        var section = CreateSection();

        for (var index = 0; index < Section.Bricks; index++)
        {
            section.SetDense(channel, index);
        }

        for (var index = 0; index < Section.Bricks; index++)
        {
            Assert.Equal(BrickType.Dense, section.GetBrickType(channel, index));
            Assert.Equal(Section.VoxelsPerBrick, section.GetDense(channel, index).Span.Length);
        }
    }

    [Fact]
    public void SetDense_ReturnsADistinctArrayPerBrick()
    {
        var section = CreateSection();

        section.SetDense(VoxelChannel.Content, 0);
        section.SetDense(VoxelChannel.Content, 1);

        var first = section.GetDense(VoxelChannel.Content, 0);
        var second = section.GetDense(VoxelChannel.Content, 1);

        Assert.NotEqual(first, second);
        Assert.False(first.Span.Overlaps(second.Span));
    }

    [Fact]
    public void SetDense_OnADenseBrick_KeepsTheSameArray()
    {
        var section = CreateSection();

        section.SetDense(VoxelChannel.Content, 5);
        var first = section.GetDense(VoxelChannel.Content, 5);

        section.SetDense(VoxelChannel.Content, 5);
        var second = section.GetDense(VoxelChannel.Content, 5);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SetDense_ClearsWithTheChannelValue()
    {
        var section = CreateSection();

        section.SetDense(VoxelChannel.Content, 5);
        section.GetDense(VoxelChannel.Content, 5).Span.Fill(123);
        section.SetDense(VoxelChannel.Content, 5);
        Assert.All(section.GetDense(VoxelChannel.Content, 5).Span.ToArray(), value => Assert.Equal(0, value));

        section.SetDense(VoxelChannel.Material, 5);
        section.GetDense(VoxelChannel.Material, 5).Span.Fill(123);
        section.SetDense(VoxelChannel.Material, 5);
        Assert.All(section.GetDense(VoxelChannel.Material, 5).Span.ToArray(), value => Assert.Equal(255, value));
    }

    [Fact]
    public void SetUniform_ReleasesTheDenseArray()
    {
        var arena = new SectionArena();
        var section = arena.Create(Origin);

        section.SetDense(VoxelChannel.Content, 5);
        var dense = section.GetDense(VoxelChannel.Content, 5);

        section.SetUniform(VoxelChannel.Content, 5, 42);

        Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, 5));
        Assert.Equal(dense, arena.AllocateVoxelArray());
    }

    [Fact]
    public void Channels_StoreDescriptorsIndependently()
    {
        var section = CreateSection();

        for (var index = 0; index < Section.Bricks; index++)
        {
            section.SetUniform(VoxelChannel.Content, index, (byte)index);
            section.SetDense(VoxelChannel.Material, index);
        }

        for (var index = 0; index < Section.Bricks; index++)
        {
            Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, index));
            Assert.Equal((byte)index, section.GetUniformValue(VoxelChannel.Content, index));
            Assert.Equal(BrickType.Dense, section.GetBrickType(VoxelChannel.Material, index));
        }

        Assert.True(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.Uniform));
        Assert.True(section.AreAllBricksOfType(VoxelChannel.Material, BrickType.Dense));
    }

    [Fact]
    public void GetBrickIndex_UsesXFastestOrder()
    {
        Assert.Equal(0, Section.GetBrickIndex(0, 0, 0));
        Assert.Equal(1, Section.GetBrickIndex(1, 0, 0));
        Assert.Equal(Section.BricksPerAxis, Section.GetBrickIndex(0, 1, 0));
        Assert.Equal(Section.BricksPerAxis * Section.BricksPerAxis, Section.GetBrickIndex(0, 0, 1));
        Assert.Equal(
            Section.Bricks - 1,
            Section.GetBrickIndex(Section.BricksPerAxis - 1, Section.BricksPerAxis - 1, Section.BricksPerAxis - 1)
        );
    }

    [Fact]
    public void AreAllBricksOfType_RequiresEveryBrickToMatch()
    {
        var section = CreateSection();

        Assert.True(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.None));
        Assert.False(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.Uniform));

        section.SetUniform(VoxelChannel.Content, 0, 0);

        Assert.False(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.None));
        Assert.False(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.Uniform));

        for (var index = 0; index < Section.Bricks; index++)
        {
            section.SetUniform(VoxelChannel.Content, index, 0);
        }

        Assert.True(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.Uniform));
        Assert.False(section.AreAllBricksOfType(VoxelChannel.Content, BrickType.None));
    }

    [Theory]
    [InlineData(VoxelChannel.Content, BrickType.None)]
    [InlineData(VoxelChannel.Content, BrickType.Uniform)]
    [InlineData(VoxelChannel.Content, BrickType.Dense)]
    [InlineData(VoxelChannel.Material, BrickType.None)]
    [InlineData(VoxelChannel.Material, BrickType.Uniform)]
    [InlineData(VoxelChannel.Material, BrickType.Dense)]
    public void AreAllBricksOfType_IsTrueOnlyForTheTypeEveryBrickHas(VoxelChannel channel, BrickType type)
    {
        var section = CreateSection();

        FillChannel(section, channel, type);

        foreach (var queried in new[] { BrickType.None, BrickType.Uniform, BrickType.Dense })
        {
            Assert.Equal(queried == type, section.AreAllBricksOfType(channel, queried));
        }
    }

    [Fact]
    public void Sections_SharingAnArenaDoNotInterfere()
    {
        var arena = new SectionArena();
        var first = arena.Create(new SectionPos(0, 0, 0));
        var second = arena.Create(new SectionPos(1, 0, 0));

        for (var index = 0; index < Section.Bricks; index++)
        {
            first.SetDense(VoxelChannel.Content, index);
        }

        Assert.True(first.AreAllBricksOfType(VoxelChannel.Content, BrickType.Dense));
        Assert.True(first.AreAllBricksOfType(VoxelChannel.Material, BrickType.None));
        Assert.True(second.AreAllBricksOfType(VoxelChannel.Content, BrickType.None));
        Assert.True(second.AreAllBricksOfType(VoxelChannel.Material, BrickType.None));
    }

    private static Section CreateSection()
    {
        return new SectionArena().Create(Origin);
    }

    private static void FillChannel(Section section, VoxelChannel channel, BrickType type)
    {
        for (var index = 0; index < Section.Bricks; index++)
        {
            switch (type)
            {
                case BrickType.None:
                    break;
                case BrickType.Uniform:
                    section.SetUniform(channel, index, 0);
                    break;
                case BrickType.Dense:
                    section.SetDense(channel, index);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }
    }
}

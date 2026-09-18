using System.Runtime.InteropServices;
using Graphics2.Core.Render;

namespace Graphics2.Tests;

public class DrawIndexedIndirectCommandTests
{
    [Fact]
    public void Layout_MatchesTheVulkanCommand()
    {
        var size = Marshal.SizeOf<DrawIndexedIndirectCommand>();

        Assert.Equal((int)DrawIndexedIndirectCommand.SizeInBytes, size);
        Assert.Equal(20, size);
    }

    [Fact]
    public void Layout_PutsEveryFieldAtItsSpecifiedOffset()
    {
        Assert.Equal(0, Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.IndexCount)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.InstanceCount)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.FirstIndex)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.VertexOffset)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.FirstInstance)).ToInt32());
    }

    [Fact]
    public void VertexOffset_IsSigned()
    {
        Assert.Equal(typeof(int), typeof(DrawIndexedIndirectCommand).GetField(nameof(DrawIndexedIndirectCommand.VertexOffset))!.FieldType);
    }
}

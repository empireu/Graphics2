using Silk.NET.Maths;

namespace Graphics2.Core.Render;

public sealed class RenderPassDescriptor
{
    public Box2D<int>? RenderArea { get; init; }

    public ColorAttachment[] ColorAttachments { get; init; } = [];

    public DepthAttachment? DepthAttachment { get; init; }
}

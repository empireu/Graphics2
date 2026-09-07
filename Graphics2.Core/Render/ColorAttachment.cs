using System.Numerics;

namespace Graphics2.Core.Render;

public sealed class ColorAttachment
{
    public required VkTexture Target { get; init; }
    public LoadOp LoadOp { get; init; } = LoadOp.Clear;
    public StoreOp StoreOp { get; init; } = StoreOp.Store;
    public Vector4 ClearValue { get; init; }
    public TextureLayout FinalLayout { get; init; } = TextureLayout.ColorAttachment;
}

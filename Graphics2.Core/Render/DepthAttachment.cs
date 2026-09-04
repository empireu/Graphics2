namespace Graphics2.Core.Render;

public sealed class DepthAttachment
{
    public required VkTexture Target { get; init; }
    public LoadOp LoadOp { get; init; } = LoadOp.Clear;
    public StoreOp StoreOp { get; init; } = StoreOp.Store;
    public float ClearDepth { get; init; } = 1.0f;
    public TextureLayout FinalLayout { get; init; } = TextureLayout.ColorAttachment;
}

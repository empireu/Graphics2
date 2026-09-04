namespace Graphics2.Core.Render;

public sealed class ColorAttachment
{
    public required VkTexture Target { get; init; }
    public LoadOp LoadOp { get; init; } = LoadOp.Clear;
    public StoreOp StoreOp { get; init; } = StoreOp.Store;
    public RgbaFloat ClearValue { get; init; }
    public TextureLayout FinalLayout { get; init; } = TextureLayout.ColorAttachment;
}

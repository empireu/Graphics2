namespace Graphics2.Core.Render;

public sealed class ColorAttachment
{
    public Texture Target { get; init; }
    public LoadOp LoadOp { get; init; } = LoadOp.Clear;
    public StoreOp StoreOp { get; init; } = StoreOp.Store;
    public RgbaFloat ClearValue { get; init; }
    public TextureLayout FinalLayout { get; init; } = TextureLayout.ColorAttachment;

    public ColorAttachment(Texture target)
    {
        Target = target;
    }
}

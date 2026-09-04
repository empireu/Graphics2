namespace Graphics2.Core.Resources;

public sealed class TextureDescription
{
    public required uint Width { get; init; }
    public required uint Height { get; init; }
    public uint Depth { get; init; } = 1;
    public required TextureFormat Format { get; init; }
    public required TextureUsage Usage { get; init; }
    public uint MipLevels { get; init; } = 1;
    public uint ArrayLayers { get; init; } = 1;
}

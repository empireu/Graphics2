// ReSharper disable UnusedMember.Global

namespace Graphics2.Core.Resources;

[Flags]
public enum TextureUsage
{
    Sampled = 1 << 0,
    ColorAttachment = 1 << 1,
    DepthAttachment = 1 << 2,
    Storage = 1 << 3,
    TransferSource = 1 << 4,
    TransferDestination = 1 << 5
}

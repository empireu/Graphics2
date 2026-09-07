using Graphics2.Core.Resources;

namespace Graphics2.Renderer.Quads;

public sealed class QuadRendererDescription
{
    public required TextureFormat ColorFormat { get; init; }
    public TextureFormat? DepthFormat { get; init; }
    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; }
}

using Graphics2.Core.Bootstrap;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

public sealed class Frame
{
    internal readonly VkDevice Device;
    internal readonly CommandBuffer CommandBuffer;
    internal readonly uint ImageIndex;

    public Texture SwapchainColorTarget { get; }

    public Vector2D<int> Extent { get; }

    internal Frame(VkDevice device, CommandBuffer commandBuffer, uint imageIndex, Texture swapchainColorTarget, Extent2D extent)
    {
        Device = device;
        CommandBuffer = commandBuffer;
        ImageIndex = imageIndex;
        SwapchainColorTarget = swapchainColorTarget;
        Extent = new Vector2D<int>((int)extent.Width, (int)extent.Height);
    }

    public RenderPassEncoder BeginRenderPass(RenderPassDescriptor descriptor) => RenderPassEncoder.Begin(Device, CommandBuffer, descriptor, Extent);
}

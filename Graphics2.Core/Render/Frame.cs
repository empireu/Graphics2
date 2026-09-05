using Graphics2.Core.Bootstrap;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

public sealed class Frame
{
    internal readonly VkDevice Device;
    internal readonly DescriptorPool DescriptorPool;
    internal readonly CommandBuffer CommandBuffer;
    internal readonly uint ImageIndex;
    private readonly UploadRing _uploadRing;

    public VkTexture SwapchainColorTarget { get; }

    public Vector2D<int> Extent { get; }

    internal Frame(VkDevice device, DescriptorPool descriptorPool, CommandBuffer commandBuffer, uint imageIndex, VkTexture swapchainColorTarget, Extent2D extent, UploadRing uploadRing)
    {
        Device = device;
        DescriptorPool = descriptorPool;
        CommandBuffer = commandBuffer;
        ImageIndex = imageIndex;
        SwapchainColorTarget = swapchainColorTarget;
        Extent = new Vector2D<int>((int)extent.Width, (int)extent.Height);
        _uploadRing = uploadRing;
    }

    public RingAllocation Allocate(uint size, uint alignment) => _uploadRing.Allocate(size, alignment);

    public RingAllocation AllocateUniform(uint size) => _uploadRing.AllocateUniform(size);

    public RenderPassEncoder BeginRenderPass(RenderPassDescriptor descriptor) => RenderPassEncoder.Begin(Device, DescriptorPool, CommandBuffer, descriptor, Extent);
}

using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

public sealed unsafe class Frame
{
    internal readonly VkDevice Device;
    internal readonly DescriptorPool DescriptorPool;
    internal readonly CommandBuffer CommandBuffer;
    internal readonly uint ImageIndex;
    private readonly UploadRing _uploadRing;
    private readonly UploadRing _vertexRing;
    private readonly HashSet<ulong> _pendingCopies = [];

    public VkTexture SwapchainColorTarget { get; }

    public Vector2D<int> Extent { get; }

    internal Frame(VkDevice device, DescriptorPool descriptorPool, CommandBuffer commandBuffer, uint imageIndex, VkTexture swapchainColorTarget, Extent2D extent, UploadRing uploadRing, UploadRing vertexRing)
    {
        Device = device;
        DescriptorPool = descriptorPool;
        CommandBuffer = commandBuffer;
        ImageIndex = imageIndex;
        SwapchainColorTarget = swapchainColorTarget;
        Extent = new Vector2D<int>((int)extent.Width, (int)extent.Height);
        _uploadRing = uploadRing;
        _vertexRing = vertexRing;
    }

    public RingAllocation Allocate(uint size, uint alignment) => _uploadRing.Allocate(size, alignment);

    public RingAllocation AllocateUniform(uint size) => _uploadRing.AllocateUniform(size);

    public RingAllocation AllocateVertex(uint size, uint alignment) => _vertexRing.Allocate(size, alignment);

    public void CopyBuffer(VkBuffer src, ulong srcOffset, VkBuffer dst, ulong dstOffset, ulong size)
    {
        var region = new BufferCopy
        {
            SrcOffset = srcOffset,
            DstOffset = dstOffset,
            Size = size
        };

        Device.Instance.Vk.CmdCopyBuffer(CommandBuffer, src.Handle, dst.Handle, 1, &region);
        _pendingCopies.Add(dst.Handle.Handle);
    }

    public void CopyBuffer(RingAllocation src, VkBuffer dst, ulong dstOffset = 0)
    {
        CopyBuffer(src.Buffer, src.Offset, dst, dstOffset, src.Size);
    }

    public RenderPassEncoder BeginRenderPass(RenderPassDescriptor descriptor) => RenderPassEncoder.Begin(Device, DescriptorPool, CommandBuffer, descriptor, Extent, _pendingCopies);
}

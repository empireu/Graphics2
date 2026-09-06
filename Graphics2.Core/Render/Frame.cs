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
    private readonly HashSet<VkTexture> _pendingImageCopies = [];

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

    public void CopyBufferToImage(VkBuffer src, ulong srcOffset, VkTexture dst, uint x, uint y, uint width, uint height)
    {
        var vk = Device.Instance.Vk;

        if (dst.CurrentLayout != ImageLayout.TransferDstOptimal)
        {
            var preBarrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.None,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = dst.CurrentLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = dst.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &preBarrier
            };

            vk.CmdPipelineBarrier2(CommandBuffer, &dependencyInfo);
            dst.CurrentLayout = ImageLayout.TransferDstOptimal;
        }

        var region = new BufferImageCopy
        {
            BufferOffset = srcOffset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D((int)x, (int)y, 0),
            ImageExtent = new Extent3D(width, height, 1)
        };

        vk.CmdCopyBufferToImage(CommandBuffer, src.Handle, dst.Image, ImageLayout.TransferDstOptimal, 1, &region);

        _pendingImageCopies.Add(dst);
    }

    public void CopyBufferToImage(RingAllocation src, VkTexture dst, uint x, uint y, uint width, uint height)
    {
        CopyBufferToImage(src.Buffer, src.Offset, dst, x, y, width, height);
    }

    public RenderPassEncoder BeginRenderPass(RenderPassDescriptor descriptor) => RenderPassEncoder.Begin(Device, DescriptorPool, CommandBuffer, descriptor, Extent, _pendingCopies, _pendingImageCopies);
}

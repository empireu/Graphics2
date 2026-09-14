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
    private readonly UploadRing _uniformRing;
    private readonly UploadRing _storageRing;
    private readonly UploadRing _geometryRing;
    private readonly HashSet<ulong> _pendingCopies;
    private readonly HashSet<VkTexture> _pendingImageCopies;
    private readonly List<IDisposable> _deferredDisposals;

    public VkTexture SwapchainColorTarget { get; }

    public Vector2D<int> Extent { get; }

    
    /// <summary>
    ///     Index of the frame slot this frame records into, in [0, FramesInFlight).
    ///     A resource that a frame writes has to be owned per slot, because the other frames in flight still read the contents of their own.
    /// </summary>
    public uint SlotIndex { get; }

    internal Frame(VkDevice device, DescriptorPool descriptorPool, CommandBuffer commandBuffer, uint imageIndex, uint slotIndex, VkTexture swapchainColorTarget, Extent2D extent, UploadRing uniformRing, UploadRing storageRing, UploadRing geometryRing, List<IDisposable> deferredDisposals, HashSet<ulong> pendingCopies, HashSet<VkTexture> pendingImageCopies)
    {
        Device = device;
        DescriptorPool = descriptorPool;
        CommandBuffer = commandBuffer;
        ImageIndex = imageIndex;
        SlotIndex = slotIndex;
        SwapchainColorTarget = swapchainColorTarget;
        Extent = new Vector2D<int>((int)extent.Width, (int)extent.Height);
        _uniformRing = uniformRing;
        _storageRing = storageRing;
        _geometryRing = geometryRing;
        _pendingCopies = pendingCopies;
        _pendingImageCopies = pendingImageCopies;
        _deferredDisposals = deferredDisposals;
    }

    /// <summary>
    ///     Queues a resource to be destroyed once the GPU has finished this frame slot, which is one full cycle of frames in flight after this call. 
    ///     Destroying a resource that an earlier frame still reads corrupts the memory its allocation was freed into.
    /// </summary>
    public void DisposeLater(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        _deferredDisposals.Add(resource);
    }

    public RingAllocation AllocateUniform(uint size) => _uniformRing.Allocate(size);

    public RingAllocation AllocateStorage(uint size) => _storageRing.Allocate(size);

    public RingAllocation AllocateVertex(uint size) => _geometryRing.Allocate(size);
    
    public RingAllocation AllocateUniform(int size) => _uniformRing.Allocate((uint)size);

    public RingAllocation AllocateStorage(int size) => _storageRing.Allocate((uint)size);

    public RingAllocation AllocateVertex(int size) => _geometryRing.Allocate((uint)size);
    
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

    public RenderPassEncoder BeginRenderPass(RenderPassDescription description) => RenderPassEncoder.Begin(Device, DescriptorPool, CommandBuffer, description, Extent, _pendingCopies, _pendingImageCopies);
}

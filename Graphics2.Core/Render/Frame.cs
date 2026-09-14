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
    internal readonly HashSet<ulong> PendingCopies;
    internal readonly HashSet<VkTexture> PendingImageCopies;
    private readonly List<IDisposable> _deferredDisposals;

    public VkTexture SwapchainColorTarget { get; }

    public Vector2D<int> Extent { get; }

    /// <summary>
    ///     Index of the frame slot this frame records into, in [0, FramesInFlight).
    ///     A resource that a frame writes has to be owned per slot, because the other frames in flight still read the contents of their own.
    /// </summary>
    public uint SlotIndex { get; }

    /// <summary>
    ///     True while a render pass is open. Barriers are illegal inside dynamic rendering, so no copy may be recorded then.
    /// </summary>
    internal bool PassOpen { get; set; }

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
        PendingCopies = pendingCopies;
        PendingImageCopies = pendingImageCopies;
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
        ValidateNotInRenderPass();

        // An earlier submission may still be reading or writing the destination, which no later barrier can order, so this barrier has to precede the copy:
        var (srcStages, srcWrites) = CopyDestinationScope(dst.Usage);

        var preBarrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = srcStages,
            SrcAccessMask = srcWrites,
            DstStageMask = PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = dst.Handle,
            Offset = 0,
            Size = Vk.WholeSize
        };

        var preDependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &preBarrier
        };

        Device.Instance.Vk.CmdPipelineBarrier2(CommandBuffer, &preDependencyInfo);

        var region = new BufferCopy
        {
            SrcOffset = srcOffset,
            DstOffset = dstOffset,
            Size = size
        };

        Device.Instance.Vk.CmdCopyBuffer(CommandBuffer, src.Handle, dst.Handle, 1, &region);
        PendingCopies.Add(dst.Handle.Handle);
    }

    public void CopyBuffer(RingAllocation src, VkBuffer dst, ulong dstOffset = 0)
    {
        CopyBuffer(src.Buffer, src.Offset, dst, dstOffset, src.Size);
    }

    public void CopyBufferToImage(VkBuffer src, ulong srcOffset, VkTexture dst, uint x, uint y, uint width, uint height)
    {
        ValidateNotInRenderPass();

        var vk = Device.Instance.Vk;

        // The image is transitioned for the copy and may still be read or written by an earlier submission, so the source scope covers every prior command touching it:
        var preBarrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
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

        var preDependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &preBarrier
        };

        vk.CmdPipelineBarrier2(CommandBuffer, &preDependencyInfo);
        dst.CurrentLayout = ImageLayout.TransferDstOptimal;

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

        PendingImageCopies.Add(dst);
    }

    public void CopyBufferToImage(RingAllocation src, VkTexture dst, uint x, uint y, uint width, uint height)
    {
        CopyBufferToImage(src.Buffer, src.Offset, dst, x, y, width, height);
    }

    public RenderPassEncoder BeginRenderPass(RenderPassDescription description) => RenderPassEncoder.Begin(this, description);

    private void ValidateNotInRenderPass()
    {
        if (PassOpen)
        {
            throw new InvalidOperationException(
                "A copy cannot be recorded while a render pass is open, because dynamic rendering forbids the barrier that has to precede it."
            );
        }
    }

    /// <summary>
    ///     The stages that read or write a copy destination, derived from its usage so that the barrier before the copy waits on that buffer's own consumers and nothing else.
    ///     The copy stage is always included, for the write of an earlier copy into the same destination.
    /// </summary>
    private static (PipelineStageFlags2 Stages, AccessFlags2 Writes) CopyDestinationScope(BufferUsage usage)
    {
        var stages = PipelineStageFlags2.CopyBit;
        var writes = AccessFlags2.TransferWriteBit;

        if ((usage & BufferUsage.Vertex) != 0)
        {
            stages |= PipelineStageFlags2.VertexAttributeInputBit;
        }

        if ((usage & BufferUsage.Index) != 0)
        {
            stages |= PipelineStageFlags2.IndexInputBit;
        }

        if ((usage & BufferUsage.Indirect) != 0)
        {
            stages |= PipelineStageFlags2.DrawIndirectBit;
        }

        if ((usage & (BufferUsage.Uniform | BufferUsage.Storage)) != 0)
        {
            stages |= PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
        }

        if ((usage & BufferUsage.Storage) != 0)
        {
            writes |= AccessFlags2.ShaderStorageWriteBit;
        }

        return (stages, writes);
    }
}

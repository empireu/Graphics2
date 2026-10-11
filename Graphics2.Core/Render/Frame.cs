using System.Buffers;
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
    private readonly FrameTimestamps? _timestamps;

    /// <summary>
    ///     The most timestamps one frame can write with <see cref="WriteTimestamp"/>.
    /// </summary>
    public const int MaxTimestamps = 64;

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

    internal Frame(VkDevice device, DescriptorPool descriptorPool, CommandBuffer commandBuffer, uint imageIndex, uint slotIndex, VkTexture swapchainColorTarget, Extent2D extent, UploadRing uniformRing, UploadRing storageRing, UploadRing geometryRing, List<IDisposable> deferredDisposals, HashSet<ulong> pendingCopies, HashSet<VkTexture> pendingImageCopies, FrameTimestamps? timestamps)
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
        _timestamps = timestamps;
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
        CopyBufferToImage(src, srcOffset, dst, x, y, 0, width, height, 1);
    }

    /// <summary>
    ///     Copies a region of a buffer into a region of an image.
    ///     The region is a single layer and a single mip level, which for a 3D image is the whole volume at the given z offset, because a volume has one array layer.
    /// </summary>
    public void CopyBufferToImage(VkBuffer src, ulong srcOffset, VkTexture dst, uint x, uint y, uint z, uint width, uint height, uint depth)
    {
        ValidateNotInRenderPass();
        RecordImageCopyBarrier(dst);

        var region = CreateImageCopy(srcOffset, x, y, z, width, height, depth);

        Device.Instance.Vk.CmdCopyBufferToImage(CommandBuffer, src.Handle, dst.Image, ImageLayout.TransferDstOptimal, 1, &region);

        PendingImageCopies.Add(dst);
    }

    public void CopyBufferToImage(RingAllocation src, VkTexture dst, uint x, uint y, uint width, uint height)
    {
        CopyBufferToImage(src.Buffer, src.Offset, dst, x, y, 0, width, height, 1);
    }

    public void CopyBufferToImage(RingAllocation src, VkTexture dst, uint x, uint y, uint z, uint width, uint height, uint depth)
    {
        CopyBufferToImage(src.Buffer, src.Offset, dst, x, y, z, width, height, depth);
    }

    /// <summary>
    ///     Copies several boxes of <paramref name="src"/> into <paramref name="dst"/> behind one barrier and in one copy command.
    ///     Vulkan forbids regions that overlap in the texture or in the source. An empty span records nothing.
    /// </summary>
    public void CopyBufferToImage(RingAllocation src, Texture dst, ReadOnlySpan<TextureCopyRegion> regions)
    {
        ValidateNotInRenderPass();

        if (regions.IsEmpty)
        {
            return;
        }

        foreach (var region in regions)
        {
            region.Validate(dst.Width, dst.Height, dst.Depth, dst.MipLevels, dst.ArrayLayers, dst.Format, src.Offset, src.Size);
        }

        RecordImageCopyBarrier(dst);

        var copies = ArrayPool<BufferImageCopy>.Shared.Rent(regions.Length);

        for (var index = 0; index < regions.Length; index++)
        {
            var region = regions[index];

            copies[index] = CreateImageCopy(src.Offset + region.SourceOffset, region.X, region.Y, region.Z, region.Width, region.Height, region.Depth, region.MipLevel, region.ArrayLayer);
        }

        fixed (BufferImageCopy* first = copies)
        {
            Device.Instance.Vk.CmdCopyBufferToImage(CommandBuffer, src.Buffer.Handle, dst.Image, ImageLayout.TransferDstOptimal, (uint)regions.Length, first);
        }

        ArrayPool<BufferImageCopy>.Shared.Return(copies);
        PendingImageCopies.Add(dst);
    }

    /// <summary>
    ///     Copies the whole of <paramref name="src"/>, a color texture with <see cref="TextureUsage.TransferSource"/>, into the start of <paramref name="dst"/> as tightly packed rows, and makes the copy visible to the host.
    ///     The texture is left in the copy's source layout, which a later pass or binding transitions away from.
    ///     The CPU may read <paramref name="dst"/> once this frame's slot comes round again, because <c>BeginFrame</c> waits for the slot's previous frame, so a buffer read that way has to be owned per slot.
    /// </summary>
    public void CopyTextureToBuffer(Texture src, VkBuffer dst)
    {
        ValidateNotInRenderPass();

        if (src.Aspect != ImageAspectFlags.ColorBit)
        {
            throw new ArgumentException("Only a color texture can be copied into a buffer.", nameof(src));
        }

        if (src.MipLevels != 1 || src.ArrayLayers != 1)
        {
            throw new ArgumentException($"Only a texture of one level and one layer can be copied whole, but this one has {src.MipLevels} levels and {src.ArrayLayers} layers.", nameof(src));
        }

        var size = (long)src.Width * src.Height * src.Depth * src.Format.TexelSize();

        if (size > dst.Size)
        {
            throw new ArgumentException($"The texture holds {size} bytes and the buffer {dst.Size}.", nameof(dst));
        }

        var vk = Device.Instance.Vk;
        var (srcStages, srcWrites) = CopyDestinationScope(dst.Usage);

        // The image may still be written by any earlier command, and the buffer by an earlier copy, so both are ordered ahead of this one:
        var imageBarrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.TransferReadBit,
            OldLayout = src.CurrentLayout,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = src.Image,
            SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.ColorBit)
        };

        var bufferBarrier = new BufferMemoryBarrier2
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
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &imageBarrier,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &bufferBarrier
        };

        vk.CmdPipelineBarrier2(CommandBuffer, &preDependencyInfo);
        src.CurrentLayout = ImageLayout.TransferSrcOptimal;

        var region = CreateImageCopy(0, 0, 0, 0, src.Width, src.Height, src.Depth);
        vk.CmdCopyImageToBuffer(CommandBuffer, src.Image, ImageLayout.TransferSrcOptimal, dst.Handle, 1, &region);

        // A fence wait makes device writes available but not visible to the host, which takes this barrier:
        var hostBarrier = new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = dst.Handle,
            Offset = 0,
            Size = Vk.WholeSize
        };

        var postDependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &hostBarrier
        };

        vk.CmdPipelineBarrier2(CommandBuffer, &postDependencyInfo);
    }

    public RenderPassEncoder BeginRenderPass(RenderPassDescription description) => RenderPassEncoder.Begin(this, description);

    /// <summary>
    ///     Marks the time at which the GPU has finished every command recorded before this call. The frame keeps <paramref name="label"/>, so it should be a constant.
    ///     <see cref="RenderContext.GpuTimings"/> reports the time from the mark before, or from the frame's start, once this frame's slot comes round again.
    ///     Does nothing on a device whose queue writes no timestamps. No pass may be open.
    /// </summary>
    public void WriteTimestamp(string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        if (PassOpen)
        {
            throw new InvalidOperationException("A timestamp marks the end of a pass, so it cannot be written while a render pass is open.");
        }

        _timestamps?.Write(CommandBuffer, label);
    }

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
    ///     Moves the image to the copy layout and orders the copy after every earlier command.
    /// </summary>
    private void RecordImageCopyBarrier(VkTexture dst)
    {
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
            SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.ColorBit)
        };

        var preDependencyInfo = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &preBarrier
        };

        Device.Instance.Vk.CmdPipelineBarrier2(CommandBuffer, &preDependencyInfo);
        dst.CurrentLayout = ImageLayout.TransferDstOptimal;
    }

    /// <summary>
    ///     A copy of tightly packed texels between a buffer and one mip level of one layer of a color image.
    /// </summary>
    private static BufferImageCopy CreateImageCopy(ulong bufferOffset, uint x, uint y, uint z, uint width, uint height, uint depth, uint mipLevel = 0, uint arrayLayer = 0)
    {
        return new BufferImageCopy
        {
            BufferOffset = bufferOffset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = mipLevel,
                BaseArrayLayer = arrayLayer,
                LayerCount = 1
            },
            ImageOffset = new Offset3D((int)x, (int)y, (int)z),
            ImageExtent = new Extent3D(width, height, depth)
        };
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

        if ((usage & (BufferUsage.Indirect | BufferUsage.IndirectHostVisible)) != 0)
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

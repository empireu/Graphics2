using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Graphics2.Core.Render;

public sealed unsafe class RenderContext : IDisposable
{
    private readonly VkDevice _device;
    private readonly FrameSlot[] _slots;
    private Semaphore[] _renderFinishedSemaphores;
    private VkTexture[] _swapchainTargets = [];
    private readonly DescriptorPool[] _descriptorPools;
    private readonly List<IDisposable>[] _deferredDisposals;

    /// <summary>
    ///     Pending copy barriers live here rather than on the frame.
    ///     A copy recorded after the last pass of a frame is only barriered at the next pass begin, which may be in the next frame, and submission order on the queue is what makes a barrier in a later command buffer cover an earlier copy.
    /// </summary>
    private readonly HashSet<ulong> _pendingCopies = [];
    private readonly HashSet<VkTexture> _pendingImageCopies = [];
    
    private uint _frame;
    private int _disposed;

    private RenderContext(
        VkDevice device,
        VkSwapchain swapchain,
        FrameSlot[] slots,
        Semaphore[] renderFinishedSemaphores,
        Extent2D extent,
        uint framesInFlight)
    {
        _device = device;
        Swapchain = swapchain;
        _slots = slots;
        _renderFinishedSemaphores = renderFinishedSemaphores;
        Extent = extent;
        FramesInFlight = framesInFlight;

        _descriptorPools = new DescriptorPool[framesInFlight];
        _deferredDisposals = new List<IDisposable>[framesInFlight];

        for (var i = 0; i < framesInFlight; i++)
        {
            _descriptorPools[i] = CreateDescriptorPool(device);
            _deferredDisposals[i] = [];
        }
    }

    public readonly VkSwapchain Swapchain;

    public Extent2D Extent { get; private set; }
    
    public readonly uint FramesInFlight;

    private const long RingCapacity = 16 * 1024 * 1024;

    public static RenderContext Create(
        VkDevice device,
        VulkanResourceFactory factory,
        SurfaceKHR surface,
        Extent2D extent,
        VkSwapchainDescription swapchainDescription,
        uint framesInFlight = 2)
    {
        var swapchain = VkSwapchain.Create(device, surface, extent, swapchainDescription);

        try
        {
            var uniformAlignment = (uint)device.Info.MinUniformBufferOffsetAlignment;
            if (uniformAlignment == 0)
            {
                uniformAlignment = 1;
            }

            var storageAlignment = (uint)device.Info.MinStorageBufferOffsetAlignment;
            if (storageAlignment == 0)
            {
                storageAlignment = 1;
            }

            const uint vertexIndexAlignment = 4;

            var slots = new FrameSlot[framesInFlight];
            for (var i = 0; i < framesInFlight; i++)
            {
                var uniformRing = UploadRing.Create(factory, RingCapacity, uniformAlignment, BufferUsage.Uniform | BufferUsage.Staging);
                var storageRing = UploadRing.Create(factory, RingCapacity, storageAlignment, BufferUsage.Storage | BufferUsage.Staging);
                var geometryRing = UploadRing.Create(factory, RingCapacity, vertexIndexAlignment, BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Staging);
                slots[i] = FrameSlot.Create(device, uniformRing, storageRing, geometryRing);
            }

            try
            {
                var semaphores = CreateRenderFinishedSemaphores(device, swapchain.ImageCount);
                var context = new RenderContext(device, swapchain, slots, semaphores, extent, framesInFlight);
                context.BuildSwapchainTargets();
                return context;
            }
            catch
            {
                foreach (var slot in slots)
                {
                    slot.Dispose();
                }

                throw;
            }
        }
        catch
        {
            swapchain.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Waits for the current frame slot to be idle, resets it, acquires a swapchain image, and begins the command buffer.
    ///     Returns a <see cref="Frame"/> for recording commands.
    /// </summary>
    public Frame BeginFrame()
    {
        var frameIndex = _frame % FramesInFlight;
        var slot = _slots[frameIndex];
        var vk = _device.Instance.Vk;

        var fence = slot.Fence;
        vk.WaitForFences(_device.Device, 1, &fence, true, ulong.MaxValue);
        vk.ResetFences(_device.Device, 1, &fence);

        RunDisposalsForFrame(frameIndex);

        slot.Reset();
        slot.UniformRing.Reset();
        slot.StorageRing.Reset();
        slot.GeometryRing.Reset();

        var descriptorPool = _descriptorPools[frameIndex];
        vk.ResetDescriptorPool(_device.Device, descriptorPool, 0);

        var result = Swapchain.AcquireNextImage(slot.AcquireSemaphore, out var imageIndex);
        
        while (result == Result.ErrorOutOfDateKhr)
        {
            Recreate(Extent);
            result = Swapchain.AcquireNextImage(slot.AcquireSemaphore, out imageIndex);
        }

        if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new VkBootstrapException("Failed to acquire swapchain image");
        }

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        if (vk.BeginCommandBuffer(slot.CommandBuffer, in beginInfo) != Result.Success)
        {
            throw new VkBootstrapException("Failed to begin command buffer");
        }

        return new Frame(_device, descriptorPool, slot.CommandBuffer, imageIndex, frameIndex, _swapchainTargets[imageIndex], Swapchain.Extent, slot.UniformRing, slot.StorageRing, slot.GeometryRing, _deferredDisposals[frameIndex], _pendingCopies, _pendingImageCopies);
    }

    /// <summary>
    ///     Ends the command buffer, submits it to the graphics queue (waiting on the slot's acquire semaphore, signaling the per-image render-finished semaphore, and setting the slot fence), then presents.
    ///     Recreates the swapchain on Suboptimal/OutOfDate.
    /// </summary>
    public void EndFrame(Frame frame)
    {
        var slot = _slots[_frame % FramesInFlight];
        var vk = _device.Instance.Vk;

        if (vk.EndCommandBuffer(slot.CommandBuffer) != Result.Success)
        {
            throw new VkBootstrapException("Failed to end command buffer");
        }

        var waitSemaphore = slot.AcquireSemaphore;
        var stageMask = PipelineStageFlags.ColorAttachmentOutputBit;
        var commandBuffer = slot.CommandBuffer;
        var renderFinished = _renderFinishedSemaphores[frame.ImageIndex];

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &stageMask,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &renderFinished
        };

        var fence = slot.Fence;
        if (vk.QueueSubmit(_device.Queue, 1, &submitInfo, fence) != Result.Success)
        {
            throw new VkBootstrapException("Failed to submit command buffer");
        }

        var presentResult = Swapchain.QueuePresent(renderFinished, frame.ImageIndex);
        if (presentResult is Result.SuboptimalKhr or Result.ErrorOutOfDateKhr)
        {
            Recreate(Extent);
        }

        _frame++;
    }

    public void Recreate(Extent2D extent)
    {
        _device.Instance.Vk.DeviceWaitIdle(_device.Device);
        DestroyRenderFinishedSemaphores();
        Swapchain.Recreate(extent);
        _renderFinishedSemaphores = CreateRenderFinishedSemaphores(_device, Swapchain.ImageCount);
        BuildSwapchainTargets();
        Extent = extent;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;
        vk.DeviceWaitIdle(_device.Device);

        RunAllDisposals();

        foreach (var pool in _descriptorPools)
        {
            vk.DestroyDescriptorPool(_device.Device, pool, null);
        }

        foreach (var sem in _renderFinishedSemaphores)
        {
            vk.DestroySemaphore(_device.Device, sem, null);
        }

        foreach (var slot in _slots)
        {
            slot.Dispose();
        }

        Swapchain.Dispose();
    }

    private void RunAllDisposals()
    {
        for (var i = 0; i < _deferredDisposals.Length; i++)
        {
            RunDisposalsForFrame((uint)i);
        }
    }

    private void RunDisposalsForFrame(uint frameIndex)
    {
        var deferred = _deferredDisposals[frameIndex];

        if (deferred.Count == 0)
        {
            return;
        }

        foreach (var resource in deferred)
        {
            if (resource is IDeviceResource deviceResource)
            {
                deviceResource.Destroy();
            }
            else
            {
                resource.Dispose();
            }
        }

        deferred.Clear();
    }

    private void DestroyRenderFinishedSemaphores()
    {
        var vk = _device.Instance.Vk;
        foreach (var sem in _renderFinishedSemaphores)
        {
            vk.DestroySemaphore(_device.Device, sem, null);
        }
    }

    private void BuildSwapchainTargets()
    {
        _swapchainTargets = new VkTexture[Swapchain.ImageCount];
        for (var i = 0; i < _swapchainTargets.Length; i++)
        {
            _swapchainTargets[i] = new VkTexture(Swapchain.Images[i], Swapchain.Views[i], ImageLayout.Undefined);
        }
    }

    private static Semaphore[] CreateRenderFinishedSemaphores(VkDevice device, uint count)
    {
        var vk = device.Instance.Vk;
        var semaphores = new Semaphore[count];
        var createInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo
        };

        for (var i = 0; i < semaphores.Length; i++)
        {
            if (vk.CreateSemaphore(device.Device, in createInfo, null, out semaphores[i]) == Result.Success)
            {
                continue;
            }
            
            for (var j = 0; j < i; j++)
            {
                vk.DestroySemaphore(device.Device, semaphores[j], null);
            }

            throw new VkBootstrapException("Failed to create render-finished semaphore");
        }

        return semaphores;
    }

    private static DescriptorPool CreateDescriptorPool(VkDevice device)
    {
        var vk = device.Instance.Vk;

        const int descriptorCount = 128;
        
        var poolSizes = stackalloc DescriptorPoolSize[5]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = descriptorCount },
            new() { Type = DescriptorType.StorageBuffer, DescriptorCount = descriptorCount },
            new() { Type = DescriptorType.SampledImage, DescriptorCount = descriptorCount },
            new() { Type = DescriptorType.Sampler, DescriptorCount = descriptorCount },
            new() { Type = DescriptorType.StorageImage, DescriptorCount = descriptorCount }
        };

        var createInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 256,
            PoolSizeCount = 5,
            PPoolSizes = poolSizes
        };

        if (vk.CreateDescriptorPool(device.Device, in createInfo, null, out var pool) != Result.Success)
        {
            throw new VkBootstrapException("Failed to create descriptor pool");
        }

        return pool;
    }
}

using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Graphics2.Core.Render;

public sealed unsafe class RenderContext : IDisposable
{
    private readonly VkDevice _device;
    private readonly FrameSlot[] _slots;
    private Semaphore[] _renderFinishedSemaphores;
    private Texture[] _swapchainTargets = [];
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
    }

    public readonly VkSwapchain Swapchain;

    public Extent2D Extent { get; private set; }
    
    public readonly uint FramesInFlight;

    public static RenderContext Create(
        VkDevice device,
        SurfaceKHR surface,
        Extent2D extent,
        VkSwapchainDescription swapchainDescription,
        uint framesInFlight = 2)
    {
        var swapchain = VkSwapchain.Create(device, surface, extent, swapchainDescription);

        try
        {
            var slots = new FrameSlot[framesInFlight];
            for (var i = 0; i < framesInFlight; i++)
            {
                slots[i] = FrameSlot.Create(device);
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
        var slot = _slots[_frame % FramesInFlight];
        var vk = _device.Instance.Vk;

        var fence = slot.Fence;
        vk.WaitForFences(_device.Device, 1, &fence, true, ulong.MaxValue);
        vk.ResetFences(_device.Device, 1, &fence);

        slot.Reset();

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

        return new Frame(_device, slot.CommandBuffer, imageIndex, _swapchainTargets[imageIndex], Swapchain.Extent);
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
        _swapchainTargets = new Texture[Swapchain.ImageCount];
        for (var i = 0; i < _swapchainTargets.Length; i++)
        {
            _swapchainTargets[i] = new Texture(Swapchain.Images[i], Swapchain.Views[i], ImageLayout.Undefined);
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
}

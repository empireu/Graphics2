using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Graphics2.Core.Render;

public sealed unsafe class FrameSlot : IDisposable
{
    private readonly CommandPool _commandPool;
    private int _disposed;

    private FrameSlot(
        VkDevice device,
        CommandPool commandPool,
        CommandBuffer commandBuffer,
        Semaphore acquireSemaphore,
        Fence fence)
    {
        Device = device;
        _commandPool = commandPool;
        CommandBuffer = commandBuffer;
        AcquireSemaphore = acquireSemaphore;
        Fence = fence;
    }

    public readonly VkDevice Device;
    public readonly CommandBuffer CommandBuffer;
    public readonly Semaphore AcquireSemaphore;
    public readonly Fence Fence;

    public static FrameSlot Create(VkDevice device)
    {
        var vk = device.Instance.Vk;

        var poolCreateInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = device.Info.ChosenGraphicsPresentFamily
        };

        if (vk.CreateCommandPool(device.Device, in poolCreateInfo, null, out var commandPool) != Result.Success)
        {
            throw new VkBootstrapException("Failed to create command pool");
        }

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        if (vk.AllocateCommandBuffers(device.Device, in allocInfo, out var commandBuffer) != Result.Success)
        {
            vk.DestroyCommandPool(device.Device, commandPool, null);
          
            throw new VkBootstrapException("Failed to allocate command buffer");
        }

        var semaphoreCreateInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo
        };

        if (vk.CreateSemaphore(device.Device, in semaphoreCreateInfo, null, out var acquireSemaphore) != Result.Success)
        {
            vk.FreeCommandBuffers(device.Device, commandPool, 1, &commandBuffer);
            vk.DestroyCommandPool(device.Device, commandPool, null);
           
            throw new VkBootstrapException("Failed to create acquire semaphore");
        }

        var fenceCreateInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        if (vk.CreateFence(device.Device, in fenceCreateInfo, null, out var fence) != Result.Success)
        {
            vk.DestroySemaphore(device.Device, acquireSemaphore, null);
            vk.FreeCommandBuffers(device.Device, commandPool, 1, &commandBuffer);
            vk.DestroyCommandPool(device.Device, commandPool, null);
           
            throw new VkBootstrapException("Failed to create fence");
        }

        return new FrameSlot(device, commandPool, commandBuffer, acquireSemaphore, fence);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = Device.Instance.Vk;
        vk.DestroyFence(Device.Device, Fence, null);
        vk.DestroySemaphore(Device.Device, AcquireSemaphore, null);
      
        var commandBuffer = CommandBuffer;
        vk.FreeCommandBuffers(Device.Device, _commandPool, 1, &commandBuffer);
        vk.DestroyCommandPool(Device.Device, _commandPool, null);
    }
}

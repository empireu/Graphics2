using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;

namespace Graphics2.Core.Resources;

public enum MemoryUsage
{
    GpuOnly,
    CpuToGpu,
    GpuToCpu
}

[Flags]
public enum BufferUsage
{
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,
    Staging = 1 << 4,
    Indirect = 1 << 5
}

public sealed class BufferDescription
{
    public required long Size { get; init; }

    public required BufferUsage Usage { get; init; }
}

public sealed unsafe class VkBuffer : IDisposable
{
    private readonly VkDevice _device;
    private readonly BlockAllocator<VulkanMemory> _allocator;
    private int _disposed;

    internal Silk.NET.Vulkan.Buffer Handle;
    internal Allocation Allocation;
    internal VulkanMemory Memory;

    public readonly long Size;
    public readonly MemoryUsage MemoryUsage;

    internal VkBuffer(
        VkDevice device,
        BlockAllocator<VulkanMemory> allocator,
        Silk.NET.Vulkan.Buffer handle,
        Allocation allocation,
        VulkanMemory memory,
        long size,
        MemoryUsage memoryUsage)
    {
        _device = device;
        _allocator = allocator;
        Handle = handle;
        Allocation = allocation;
        Memory = memory;
        Size = size;
        MemoryUsage = memoryUsage;
    }

    public Span<byte> MappedData
    {
        get
        {
            if (!Memory.IsMapped)
            {
                throw new InvalidOperationException("Buffer is not host-visible.");
            }

            return new Span<byte>((void*)(Memory.MappedBase + Allocation.Offset), (int)Allocation.Size);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;
        
        vk.DeviceWaitIdle(_device.Device);
        vk.DestroyBuffer(_device.Device, Handle, null);
        
        _allocator.Free(Allocation);
    }
}
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
    /// <summary>
    ///     GPU-only device-local vertex data read by the vertex shader.
    /// </summary>
    Vertex = 1 << 0,
    /// <summary>
    ///     GPU-only device-local index data read by indexed draw calls.
    /// </summary>
    Index = 1 << 1,
    /// <summary>
    ///     CPU-to-GPU host-visible UBO updated frequently per-frame.
    /// </summary>
    Uniform = 1 << 2,
    /// <summary>
    ///     GPU-only device-local SSBO read/written to by shaders.
    /// </summary>
    Storage = 1 << 3,
    /// <summary>
    ///     CPU-to-GPU host-visible transfer source, mapped for CPU writes.
    /// </summary>
    Staging = 1 << 4,
    /// <summary>
    ///     GPU-only device-local draw arguments consumed by indirect commands.
    /// </summary>
    Indirect = 1 << 5,
    /// <summary>
    ///     GPU-only device-local buffer that can receive copies.
    /// </summary>
    TransferDestination = 1 << 6,
    /// <summary>
    ///     Host-visible draw arguments rewritten by the CPU every frame, one command per draw.
    /// </summary>
    IndirectHostVisible = 1 << 7
}

public sealed class BufferDescription
{
    public required long Size { get; init; }

    public required BufferUsage Usage { get; init; }
}

public sealed unsafe class VkBuffer : IDeviceResource
{
    private readonly VkDevice _device;
    private readonly BlockAllocator<VulkanMemory> _allocator;
    private int _disposed;

    internal Silk.NET.Vulkan.Buffer Handle;
    internal Allocation Allocation;
    internal VulkanMemory Memory;

    public readonly long Size;
    public readonly MemoryUsage MemoryUsage;
    public readonly BufferUsage Usage;

    internal VkBuffer(
        VkDevice device,
        BlockAllocator<VulkanMemory> allocator,
        Silk.NET.Vulkan.Buffer handle,
        Allocation allocation,
        VulkanMemory memory,
        long size,
        MemoryUsage memoryUsage,
        BufferUsage usage)
    {
        _device = device;
        _allocator = allocator;
        Handle = handle;
        Allocation = allocation;
        Memory = memory;
        Size = size;
        MemoryUsage = memoryUsage;
        Usage = usage;
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
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _device.WaitIdle();
        
        Destroy();
    }

    public void Destroy()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;

        vk.DestroyBuffer(_device.Device, Handle, null);

        _allocator.Free(Allocation);
    }
}
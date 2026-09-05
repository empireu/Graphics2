using System.Runtime.CompilerServices;
using Graphics2.Core.Resources;

namespace Graphics2.Core.Render;

public readonly struct RingAllocation(VkBuffer buffer, uint offset, uint size)
{
    public readonly VkBuffer Buffer = buffer;
    public readonly uint Offset = offset;
    public readonly uint Size = size;
 
    public Span<byte> Data => Buffer.MappedData.Slice((int)Offset, (int)Size);
}

public sealed class UploadRing : IDisposable
{
    private readonly VkBuffer _buffer;
    private readonly uint _capacity;
    private readonly uint _uniformAlignment;
    private uint _offset;
    private int _disposed;

    private UploadRing(VkBuffer buffer, uint capacity, uint uniformAlignment)
    {
        _buffer = buffer;
        _capacity = capacity;
        _uniformAlignment = uniformAlignment;
        _offset = 0;
    }

    public static UploadRing Create(VulkanResourceFactory factory, long capacity, uint uniformAlignment)
    {
        var buffer = factory.CreateBuffer(new BufferDescription
        {
            Size = capacity,
            Usage = BufferUsage.Uniform | BufferUsage.Storage | BufferUsage.Staging
        });

        return new UploadRing(buffer, (uint)capacity, uniformAlignment);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingAllocation Allocate(uint size, uint alignment)
    {
        var alignedOffset = AlignUp(_offset, alignment);

        if (alignedOffset + size > _capacity)
        {
            throw new InvalidOperationException($"Upload ring exhausted: requested {size} bytes at offset {alignedOffset}, capacity is {_capacity}.");
        }

        _offset = alignedOffset + size;

        return new RingAllocation(_buffer, alignedOffset, size);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingAllocation AllocateUniform(uint size)
    {
        return Allocate(size, _uniformAlignment);
    }

    public void Reset()
    {
        _offset = 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _buffer.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AlignUp(uint value, uint alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        return (value + alignment - 1) & ~(alignment - 1);
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;

namespace Graphics2.Core.Resources;

/// <summary>
///     A device-local vertex and index buffer pair sized in elements.
///     Rebuild replaces the contents and grows the pair when they no longer fit, so a stream of similarly sized rebuilds stops allocating after the first few.
///     The mesh owns no managed storage.
/// </summary>
public sealed class Mesh<TVertex> : IDeviceResource where TVertex : unmanaged
{
    private readonly VkDevice _device;
    private readonly VulkanResourceFactory _factory;
    private readonly int _initialVertexCapacity;
    private readonly int _initialIndexCapacity;
    private int _disposed;

    /// <summary>
    ///     Vertex buffer, or null before the first non-empty rebuild.
    /// </summary>
    public VkBuffer? VertexBuffer { get; private set; }

    /// <summary>
    ///     Index buffer, or null before the first non-empty rebuild.
    /// </summary>
    public VkBuffer? IndexBuffer { get; private set; }

    /// <summary>
    ///     Allocated vertex capacity in elements, zero before the first non-empty rebuild.
    /// </summary>
    public int VertexCapacity { get; private set; }

    /// <summary>
    ///     Allocated index capacity in elements, zero before the first non-empty rebuild.
    /// </summary>
    public int IndexCapacity { get; private set; }

    /// <summary>
    ///     Number of vertices the last rebuild copied, zero if it had none.
    /// </summary>
    public int VertexCount { get; private set; }

    /// <summary>
    ///     Number of indices the last rebuild copied.
    /// </summary>
    public int IndexCount { get; private set; }

    public IndexType IndexType => IndexType.UInt32;

    internal Mesh(VkDevice device, VulkanResourceFactory factory, int vertexCapacity, int indexCapacity)
    {
        _device = device;
        _factory = factory;
        _initialVertexCapacity = vertexCapacity;
        _initialIndexCapacity = indexCapacity;
    }

    /// <summary>
    ///     Replaces the contents and sets index count to the number of indices copied.
    ///     An empty rebuild keeps the allocated capacity and empty contents, so a caller with nothing to draw can call this safely.
    ///     Grows the buffers when the contents do not fit, retiring the old pair through the frame's disposal queue.
    /// </summary>
    public void Rebuild(ReadOnlySpan<TVertex> vertices, ReadOnlySpan<uint> indices, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (vertices.Length == 0 || indices.Length == 0)
        {
            VertexCount = 0;
            IndexCount = 0;
            return;
        }

        if (!HasCapacity(vertices.Length, indices.Length))
        {
            Grow(vertices.Length, indices.Length, frame);
        }

        // We'll upload through the ring, and the pre-pass barrier covers VertexAttributeRead and IndexRead for every copied destination.
        var vertexBytes = MemoryMarshal.AsBytes(vertices);
        var vertexAllocation = frame.AllocateVertex(vertexBytes.Length);
        vertexBytes.CopyTo(vertexAllocation.Data);
        frame.CopyBuffer(vertexAllocation, VertexBuffer!);

        var indexBytes = MemoryMarshal.AsBytes(indices);
        var indexAllocation = frame.AllocateVertex(indexBytes.Length);
        indexBytes.CopyTo(indexAllocation.Data);
        frame.CopyBuffer(indexAllocation, IndexBuffer!);

        VertexCount = vertices.Length;
        IndexCount = indices.Length;
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

        VertexBuffer?.Destroy();
        IndexBuffer?.Destroy();
    }

    private bool HasCapacity(int vertexCount, int indexCount)
    {
        return VertexBuffer != null
            && IndexBuffer != null
            && vertexCount <= VertexCapacity
            && indexCount <= IndexCapacity;
    }

    private void Grow(int vertexCount, int indexCount, Frame frame)
    {
        var newVertexCapacity = NextCapacity(VertexCapacity, Math.Max(vertexCount, _initialVertexCapacity));
        var newIndexCapacity = NextCapacity(IndexCapacity, Math.Max(indexCount, _initialIndexCapacity));

        var newVertexBuffer = _factory.CreateBuffer(new BufferDescription
        {
            Size = (long)newVertexCapacity * Unsafe.SizeOf<TVertex>(),
            Usage = BufferUsage.Vertex | BufferUsage.TransferDestination
        });

        VkBuffer newIndexBuffer;

        try
        {
            newIndexBuffer = _factory.CreateBuffer(new BufferDescription
            {
                Size = (long)newIndexCapacity * sizeof(uint),
                Usage = BufferUsage.Index | BufferUsage.TransferDestination
            });
        }
        catch
        {
            // The new vertex buffer has never been submitted, so it can be destroyed without waiting:
            newVertexBuffer.Destroy();
            throw;
        }

        if (VertexBuffer != null)
        {
            frame.DisposeLater(VertexBuffer);
        }

        if (IndexBuffer != null)
        {
            frame.DisposeLater(IndexBuffer);
        }

        VertexBuffer = newVertexBuffer;
        IndexBuffer = newIndexBuffer;
        VertexCapacity = newVertexCapacity;
        IndexCapacity = newIndexCapacity;
    }

    private static int NextCapacity(int current, int required)
    {
        var capacity = Math.Max(current, 1);
        
        while (capacity < required)
        {
            if (capacity >= int.MaxValue / 2)
            {
                return required;
            }

            capacity *= 2;
        }

        return capacity;
    }
}

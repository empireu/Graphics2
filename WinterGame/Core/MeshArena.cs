using System.Diagnostics;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Mathematics;

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame.Core;

public sealed class MeshArenaDescription
{
    public readonly struct BufferClass
    {
        /// <summary>
        ///     The number of vertices or indices expected for the bucket.
        /// </summary>
        public required int ElementsPerSlot { get; init; }
        
        /// <summary>
        ///     The number of slots in each page.
        /// </summary>
        public required int SlotsPerPage { get; init; }
    }
    
    /// <summary>
    ///     The vertex bucket sizes that will fit the usage.
    ///     Each class's element count must give a whole number of 4-byte-aligned bytes with
    ///     <see cref="VertexStride" />, so that a slot's byte offset follows from its element offset.
    /// </summary>
    public required BufferClass[] VertexClasses { get; init; } 
    
    /// <summary>
    ///     The index bucket sizes that will fit the usage.
    ///     Each class's element count must give a whole number of 4-byte-aligned bytes with the stride of
    ///     <see cref="IndexType" />, so that a slot's byte offset follows from its element offset.
    /// </summary>
    public required BufferClass[] IndexClasses { get; init; }
    
    /// <summary>
    ///     The size of the vertex, in bytes.
    /// </summary>
    public required int VertexStride { get; init; }
    
    /// <summary>
    ///     The width of the indices.
    /// </summary>
    public IndexType IndexType { get; init; }
}

/// <summary>
///     Information about the vertex or index allocation for a mesh.
/// </summary>
public readonly struct MeshBufferHandle(VkBuffer buffer, int allocator, int page, int slot, int baseAddress, int elementOffset) : IEquatable<MeshBufferHandle>
{
    public readonly VkBuffer Buffer = buffer;

    /// <summary>
    ///     Index of the allocator that owns the region.
    /// </summary>
    public readonly int Allocator = allocator;

    /// <summary>
    ///     Index of the page, counted inside the allocator.
    /// </summary>
    public readonly int Page = page;

    /// <summary>
    ///     Index of the slot, counted inside the page.
    /// </summary>
    public readonly int Slot = slot;

    /// <summary>
    ///     Byte offset of the region in the buffer, which is where an upload writes.
    ///     It is the element offset times the stride, because a class's element count is required to give an aligned slot size.
    /// </summary>
    public readonly int BaseAddress = baseAddress;

    /// <summary>
    ///     Offset of the region in elements, which is the vertex offset or the first index a command needs.
    /// </summary>
    public readonly int ElementOffset = elementOffset;

    public bool Equals(MeshBufferHandle other)
    {
        return Buffer.Equals(other.Buffer) && Allocator == other.Allocator && Page == other.Page && Slot == other.Slot;
    }

    public override bool Equals(object? obj)
    {
        return obj is MeshBufferHandle other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Buffer, Allocator, Page, Slot);
    }

    public static bool operator ==(MeshBufferHandle left, MeshBufferHandle right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(MeshBufferHandle left, MeshBufferHandle right)
    {
        return !left.Equals(right);
    }
}

/// <summary>
///     Rented slot for the data of a mesh.
/// </summary>
public readonly struct MeshAllocation(MeshBufferHandle vertices, MeshBufferHandle indices)
{
    public readonly MeshBufferHandle Vertices = vertices;

    public readonly MeshBufferHandle Indices = indices;
}

/// <summary>
///     How much of one domain's arena is in use.
/// </summary>
public readonly struct MeshBufferStats(int pages, long bytes, int usedSlots, int freeSlots)
{
    public readonly int Pages = pages;

    public readonly long Bytes = bytes;

    public readonly int UsedSlots = usedSlots;

    public readonly int FreeSlots = freeSlots;
}

/// <summary>
///     How much of an arena is in use, per domain, because the two ladders are sized independently.
/// </summary>
public readonly struct MeshArenaStats(MeshBufferStats vertices, MeshBufferStats indices)
{
    public readonly MeshBufferStats Vertices = vertices;

    public readonly MeshBufferStats Indices = indices;

    public long Bytes => Vertices.Bytes + Indices.Bytes;
}

public sealed class MeshArena : IDisposable
{
    private sealed class BlockAllocator : IDisposable
    {
        /// <summary>
        ///     Creates a new allocator for the specified usage and element size.
        ///     The slot's element count must give a whole number of 4-byte-aligned bytes, so that a slot's byte offset is its element offset times the stride.
        ///     This is simpler than just aligning it internally and it's not restricting.
        /// </summary>
        public static BlockAllocator Create(
            VulkanResourceFactory factory,
            BufferUsage usage,
            int allocatorIndex,
            int elementsPerSlot,
            int elementStride,
            int slotsPerPage)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(allocatorIndex);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementsPerSlot);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementStride);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotsPerPage);

            var bytesPerSlot = elementsPerSlot * elementStride;

            if (MathX.AlignUp2(bytesPerSlot, 4) - bytesPerSlot != 0)
            {
                throw new ArgumentException(
                    $"A slot of {elementsPerSlot} elements at {elementStride} bytes is {bytesPerSlot} bytes, which is not a multiple of 4. Pick an element count whose bytes are 4-byte aligned so a slot's byte offset follows from its element offset.",
                    nameof(elementsPerSlot)
                );
            }

            return new BlockAllocator(
                factory,
                usage,
                allocatorIndex,
                elementsPerSlot,
                elementStride,
                bytesPerSlot,
                slotsPerPage
            );
        }

        private readonly List<VkBuffer> _pages = [];
        private readonly Stack<MeshBufferHandle> _freeSlots = [];

#if DEBUG
        private readonly HashSet<MeshBufferHandle> _inUse = [];
#endif

        private readonly VulkanResourceFactory _factory;
        private readonly BufferUsage _usage;

        /// <summary>
        ///     Index of this allocator in the arena, which every handle it hands out carries.
        /// </summary>
        public readonly int AllocatorIndex;
        
        /// <summary>
        ///     The number of vertices or indices per slot.
        /// </summary>
        public readonly int ElementsPerSlot;
        
        /// <summary>
        ///     The size of the slot, in bytes, which is the element count times the stride.
        /// </summary>
        public readonly int BytesPerSlot;

        /// <summary>
        ///     The number of bytes per element, which turns an element offset into a byte address.
        /// </summary>
        public readonly int ElementStride;
        
        /// <summary>
        ///     The number of slots per individual buffer allocation.
        /// </summary>
        public readonly int SlotsPerPage;

        /// <summary>
        ///     Allocator for fixed-size slots from buffers. The buffers are created as needed.
        ///     <paramref name="bytesPerSlot"/> The size of the slot, in bytes.
        ///     <paramref name="slotsPerPage"/> The number of slots in each individual buffer.
        /// </summary>
        private BlockAllocator(VulkanResourceFactory factory, BufferUsage usage, int allocatorIndex, int elementsPerSlot, int elementStride, int bytesPerSlot, int slotsPerPage)
        {
            _factory = factory;
            _usage = usage;
            AllocatorIndex = allocatorIndex;
            ElementsPerSlot = elementsPerSlot;
            ElementStride = elementStride;
            BytesPerSlot = bytesPerSlot;
            SlotsPerPage = slotsPerPage;
        }

        /// <summary>
        ///     The number of bytes this allocator has handed to the driver.
        /// </summary>
        public long AllocatedBytes => (long)_pages.Count * BytesPerSlot * SlotsPerPage;

        /// <summary>
        ///     The number of slots that are handed out.
        /// </summary>
        public int UsedSlots => _pages.Count * SlotsPerPage - _freeSlots.Count;

        public int PageCount => _pages.Count;

        public int FreeSlotCount => _freeSlots.Count;

        /// <summary>
        ///     Gets a free slot if any exist, or allocates a new page and returns the first slot.
        /// </summary>
        /// <returns></returns>
        public MeshBufferHandle Allocate()
        {
            if (!_freeSlots.TryPop(out var freeSlot))
            {
                var buffer = _factory.CreateBuffer(new BufferDescription
                {
                    Size = BytesPerSlot * SlotsPerPage,
                    Usage = _usage
                });
                
                _pages.Add(buffer);

                var page = _pages.Count - 1;
                
                for (var i = SlotsPerPage - 1; i >= 0; i--)
                {
                    _freeSlots.Push(CreateHandle(buffer, page, i));
                }

                freeSlot = _freeSlots.Pop();
            }

#if DEBUG
            Debug.Assert(_inUse.Add(freeSlot));
#endif

            return freeSlot;
        }

        /// <summary>
        ///     Marks the slot as freed. Safe to call anytime, as the disposal is deferred.
        /// </summary>
        /// <param name="frame"></param>
        /// <param name="slot"></param>
        public void MarkFree(Frame frame, MeshBufferHandle slot)
        {
#if DEBUG
            Debug.Assert(_pages[slot.Page] == slot.Buffer);
            Debug.Assert(_inUse.Remove(slot));
#endif
            
            frame.DisposeLater(new FreeSlotReturnHandler(_freeSlots, slot));
        }

        private MeshBufferHandle CreateHandle(VkBuffer buffer, int page, int slot)
        {
            var elementOffset = slot * ElementsPerSlot;

            return new MeshBufferHandle(buffer, AllocatorIndex, page, slot, elementOffset * ElementStride, elementOffset);
        }

        private sealed class FreeSlotReturnHandler(Stack<MeshBufferHandle> freeSlots, MeshBufferHandle slot) : IDisposable
        {
            public void Dispose()
            {
                freeSlots.Push(slot);
            }
        }

        public void Dispose()
        {
            foreach (var vkBuffer in _pages)
            {
                vkBuffer.Dispose();
            }
        }
    }

    private readonly BlockAllocator[] _vertexAllocators;
    private readonly BlockAllocator[] _indexAllocators;
    
    public MeshArena(VulkanResourceFactory factory, MeshArenaDescription description)
    {
        ValidateDescription(description);

        _vertexAllocators = new BlockAllocator[description.VertexClasses.Length];
        _indexAllocators = new BlockAllocator[description.IndexClasses.Length];

        for (var i = 0; i < description.VertexClasses.Length; i++)
        {
            var vertexClass = description.VertexClasses[i];

            _vertexAllocators[i] = BlockAllocator.Create(
                factory,
                BufferUsage.Vertex | BufferUsage.TransferDestination,
                i,
                vertexClass.ElementsPerSlot,
                description.VertexStride,
                vertexClass.SlotsPerPage
            );
        }
        
        for (var i = 0; i < description.IndexClasses.Length; i++)
        {
            var indexClass = description.IndexClasses[i];

            _indexAllocators[i] = BlockAllocator.Create(
                factory,
                BufferUsage.Index | BufferUsage.TransferDestination,
                i,
                indexClass.ElementsPerSlot,
                description.IndexType.Stride(),
                indexClass.SlotsPerPage
            );
        }
    }

    private static void ValidateDescription(MeshArenaDescription description)
    {
        if (description.VertexClasses == null || description.VertexClasses.Length == 0)
        {
            throw new ArgumentException("Mesh arena must have non-empty vertex class array");
        }
        
        if (description.IndexClasses == null || description.IndexClasses.Length == 0)
        {
            throw new ArgumentException("Mesh arena must have non-empty index class array");
        }

        if (description.VertexClasses.Concat(description.IndexClasses).Any(descriptionVertexClass => descriptionVertexClass.ElementsPerSlot <= 0 || descriptionVertexClass.SlotsPerPage <= 0))
        {
            throw new Exception("Mesh arena must have positive bucket size and page capacity");
        }
        
        for (var i = 1; i < description.VertexClasses.Length; i++)
        {
            if (description.VertexClasses[i - 1].ElementsPerSlot >= description.VertexClasses[i].ElementsPerSlot)
            {
                throw new ArgumentException("Mesh arena must have unique and ascending vertex bucket sizes");
            }
        }
        
        for (var i = 1; i < description.IndexClasses.Length; i++)
        {
            if (description.IndexClasses[i - 1].ElementsPerSlot >= description.IndexClasses[i].ElementsPerSlot)
            {
                throw new ArgumentException("Mesh arena must have unique and ascending index bucket sizes");
            }
        }

        if (description.VertexStride <= 0)
        {
            throw new Exception("Mesh arena vertex stride must be positive");
        }
    }

    public MeshAllocation Allocate(int vertexCount, int indexCount)
    {
        if (!TrySelectAllocator(_vertexAllocators, vertexCount, out var vertexAllocatorIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(vertexCount), $"Vertex count {vertexCount} exceeds arena limits");
        }

        if (!TrySelectAllocator(_indexAllocators, indexCount, out var indexAllocatorIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(indexCount), $"Index count {indexCount} exceeds arena limits");
        }

        var vertexAllocation = _vertexAllocators[vertexAllocatorIndex].Allocate();
        var indexAllocation = _indexAllocators[indexAllocatorIndex].Allocate();

        return new MeshAllocation(vertexAllocation, indexAllocation);
    }

    public void MarkFree(Frame frame, MeshAllocation allocation)
    {
        _vertexAllocators[allocation.Vertices.Allocator].MarkFree(frame, allocation.Vertices);
        _indexAllocators[allocation.Indices.Allocator].MarkFree(frame, allocation.Indices);
    }

    /// <summary>
    ///     What the arena is holding, for the debug overlay.
    /// </summary>
    public MeshArenaStats GetStats()
    {
        return new MeshArenaStats(Sum(_vertexAllocators), Sum(_indexAllocators));
    }

    private static MeshBufferStats Sum(BlockAllocator[] allocators)
    {
        var pages = 0;
        var bytes = 0L;
        var used = 0;
        var free = 0;

        foreach (var allocator in allocators)
        {
            pages += allocator.PageCount;
            bytes += allocator.AllocatedBytes;
            used += allocator.UsedSlots;
            free += allocator.FreeSlotCount;
        }

        return new MeshBufferStats(pages, bytes, used, free);
    }
    
    private static bool TrySelectAllocator(BlockAllocator[] allocators, int elementCount, out int result)
    {
        for (var index = 0; index < allocators.Length; index++)
        {
            var blockAllocator = allocators[index];

            if (elementCount <= blockAllocator.ElementsPerSlot)
            {
                result = index;
                return true;
            }
        }

        result = -1;
        return false;
    }
    
    public void Dispose()
    {
        foreach (var allocator in _vertexAllocators.Concat(_indexAllocators))
        {
            allocator.Dispose();
        }
    }
}
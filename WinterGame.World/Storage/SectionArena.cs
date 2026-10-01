using System.Diagnostics;
using System.Runtime.CompilerServices;

// ReSharper disable ConvertToPrimaryConstructor

namespace WinterGame.World.Storage;

public sealed class SectionArena
{
    /// <summary>
    ///     Bytes of descriptor storage per page in the default arena, from which the array count is derived.
    /// </summary>
    private const int DefaultDescriptorPageBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Number of section descriptor arrays one page holds in the default arena.
    /// </summary>
    public const int DefaultDescriptorArraysPerPage = DefaultDescriptorPageBytes / (Section.DescriptorsPerSection * sizeof(uint));

    /// <summary>
    ///     Bytes of voxel data per page in the default arena, from which the array count is derived.
    ///     The arena addresses 32 pages, so this also sets its capacity, 256 MiB of dense bricks at 8 MiB.
    /// </summary>
    private const int DefaultVoxelPageBytes = 8 * 1024 * 1024;

    /// <summary>
    ///     Number of one-brick voxel arrays one page holds in the default arena.
    /// </summary>
    public const int DefaultVoxelArraysPerPage = DefaultVoxelPageBytes / Section.VoxelsPerBrick;

    /// <summary>
    ///     The number of bits allocated to the page number.
    /// </summary>
    public const int PageNumberBits = 5;
    public const int MaxPageNumber = (1 << PageNumberBits) - 1;
    
    /// <summary>
    ///     The number of bits allocated to the page's address (since we have many bits to work with, this is directly in the array's space).
    /// </summary>
    public const int AddressNumberBits = BrickDescriptor.BrickUserDataBits - PageNumberBits;
    public const int MaxAddressNumber = (1 << AddressNumberBits) - 1;

    /// <summary>
    ///     The most voxel arrays one page can hold while the last array's byte address still fits in <see cref="AddressNumberBits"/>.
    /// </summary>
    public const int MaxVoxelArraysPerPage = MaxAddressNumber / Section.VoxelsPerBrick + 1;

    private readonly int _descriptorArraysPerPage;
    private readonly int _voxelArraysPerPage;

    private readonly List<BrickDescriptor[]> _descriptorPages = [];

    /// <summary>
    ///     Free descriptor arrays by page and address.
    ///     Neither free list holds a reference, since storing a struct that holds one into a heap array costs a GC write barrier per push.
    /// </summary>
    private readonly Stack<(int PageIndex, int Address)> _freeDescriptorArrays = [];

    private readonly List<byte[]> _voxelPages = [];

    /// <summary>
    ///     Free voxel arrays as packed <see cref="VoxelArrayHandle"/>s, the same value a dense descriptor carries as its user data.
    /// </summary>
    private readonly Stack<uint> _freeVoxelArrays = [];

#if DEBUG
    private readonly HashSet<DescriptorArray> _descriptorArraysInUse = [];
    private readonly HashSet<uint> _voxelArraysInUse = [];
#endif

    private readonly Dictionary<ulong, Section> _sections = [];

    /// <summary>
    ///     Creates an arena whose pages hold the given number of arrays.
    ///     An arena sized to one section, with one descriptor array and <see cref="Section.DescriptorsPerSection"/> voxel arrays per page, never grows past its first pages.
    /// </summary>
    public SectionArena(
        int descriptorArraysPerPage = DefaultDescriptorArraysPerPage,
        int voxelArraysPerPage = DefaultVoxelArraysPerPage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(descriptorArraysPerPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(voxelArraysPerPage, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(voxelArraysPerPage, MaxVoxelArraysPerPage);

        _descriptorArraysPerPage = descriptorArraysPerPage;
        _voxelArraysPerPage = voxelArraysPerPage;
    }

    internal int DescriptorPageCount => _descriptorPages.Count;

    internal int VoxelPageCount => _voxelPages.Count;

    /// <summary>
    ///     The most dense bricks the arena can hold, which is every page number a dense handle can address.
    ///     Allocating past it throws.
    /// </summary>
    public long MaxVoxelArrays => (long)(MaxPageNumber + 1) * _voxelArraysPerPage;

    private void AllocateDescriptorPage()
    {
        var capacity = Section.DescriptorsPerSection * _descriptorArraysPerPage;
        
        var page = new BrickDescriptor[capacity];
        var pageIndex = _descriptorPages.Count;
        
        _descriptorPages.Add(page);

        for (var i = capacity - Section.DescriptorsPerSection; i >= 0; i -= Section.DescriptorsPerSection)
        {
           _freeDescriptorArrays.Push((pageIndex, i));
        }
    }

    internal DescriptorArray AllocateDescriptorArray()
    {
        if (!_freeDescriptorArrays.TryPop(out var slot))
        {
            AllocateDescriptorPage();

            slot = _freeDescriptorArrays.Pop();
        }

        var array = new DescriptorArray(_descriptorPages[slot.PageIndex], slot.PageIndex, slot.Address);

#if DEBUG
        Debug.Assert(_descriptorArraysInUse.Add(array));
#endif

        return array;
    }

    internal void FreeDescriptorArray(DescriptorArray array)
    {
#if DEBUG
        Debug.Assert(_descriptorArraysInUse.Remove(array));
#endif

        _freeDescriptorArrays.Push((array.PageIndex, array.Address));
    }

    private void AllocateVoxelPage()
    {
        // A page number past the packed width would alias another page's address bits, so the arena refuses to grow instead:
        if (_voxelPages.Count > MaxPageNumber)
        {
            throw new InvalidOperationException(
                $"The arena is full: {_voxelPages.Count} voxel pages of {_voxelArraysPerPage} arrays hold {MaxVoxelArrays} dense bricks, and a dense handle cannot address another page."
            );
        }

        var capacity = Section.VoxelsPerBrick * _voxelArraysPerPage;
        
        var page = new byte[capacity];
        var pageIndex = _voxelPages.Count;
        
        _voxelPages.Add(page);

        for (var i = capacity - Section.VoxelsPerBrick; i >= 0; i -= Section.VoxelsPerBrick)
        {
            _freeVoxelArrays.Push(new VoxelArrayHandle(pageIndex, i).Pack());
        }
    }

    internal VoxelArray AllocateVoxelArray()
    {
        if (!_freeVoxelArrays.TryPop(out var packedHandle))
        {
            AllocateVoxelPage();

            packedHandle = _freeVoxelArrays.Pop();
        }

#if DEBUG
        Debug.Assert(_voxelArraysInUse.Add(packedHandle));
#endif

        var handle = VoxelArrayHandle.Unpack(packedHandle);

        return new VoxelArray(_voxelPages[handle.PageIndex], handle.PageIndex, handle.Address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal VoxelArray GetVoxelArray(VoxelArrayHandle handle)
    {
        var page = _voxelPages[handle.PageIndex];
        var result = new VoxelArray(page, handle.PageIndex, handle.Address);

#if DEBUG
        Debug.Assert(_voxelArraysInUse.Contains(handle.Pack()));
#endif

        return result;
    }

    internal void FreeVoxelArray(VoxelArray array)
    {
        FreeVoxelArray(array.Handle.Pack());
    }

    /// <summary>
    ///     Frees an array by its packed handle, which is the user data of the dense descriptor that held it.
    /// </summary>
    internal void FreeVoxelArray(uint packedHandle)
    {
#if DEBUG
        Debug.Assert(_voxelArraysInUse.Remove(packedHandle));
#endif

        _freeVoxelArrays.Push(packedHandle);
    }

    public Section Create(SectionPos pos)
    {
        var descriptorArray = AllocateDescriptorArray();

        descriptorArray.Span.Fill(BrickDescriptor.Pack(BrickType.None, 0));

        var section = new Section(this, pos, descriptorArray);

        if (!_sections.TryAdd(pos.GetKey(), section))
        {
            throw new InvalidOperationException($"Tried to create duplicate section at {pos}");
        }

        return section;
    }

    /// <summary>
    ///     Creates a section in this arena at the source's position, holding a copy of the source's bricks.
    ///     Uniform descriptors are copied as they are, and every dense brick gets an array from this arena, since a dense handle is only meaningful in the arena that allocated it.
    ///     Every brick of the source has to be written. The source is not modified.
    /// </summary>
    public Section CreateCopy(Section source)
    {
        var destination = Create(source.Position);

        var sourceDescriptors = source.DescriptorArray.Span;
        var destinationDescriptors = destination.DescriptorArray.Span;

        try
        {
            for (var index = 0; index < Section.DescriptorsPerSection; index++)
            {
                var descriptor = sourceDescriptors[index];

                switch (descriptor.Type)
                {
                    case BrickType.Uniform:
                        destinationDescriptors[index] = descriptor;
                        break;
                    case BrickType.Dense:
                        var sourceArray = source.Arena.GetVoxelArray(VoxelArrayHandle.Unpack(descriptor.UserData));
                        var destinationArray = AllocateVoxelArray();

                        sourceArray.Span.CopyTo(destinationArray.Span);
                        destinationDescriptors[index] = BrickDescriptor.Pack(BrickType.Dense, destinationArray.Handle.Pack());
                        break;
                    default:
                        throw new InvalidOperationException($"Tried to copy section {source.Position}, whose descriptor {index} is {descriptor.Type}");
                }
            }
        }
        catch
        {
            // A failed copy leaves no partial section behind, and the bricks it did not reach are still None, which frees nothing:
            Destroy(destination.Position);
            throw;
        }

        return destination;
    }

    public void Destroy(SectionPos pos)
    {
        if (!_sections.Remove(pos.GetKey(), out var value))
        {
            throw new InvalidOperationException($"Tried to destroy unknown section at {pos}");
        }

        // Only a dense brick holds storage, so one pass over both channels' descriptors finds every array to free:
        foreach (var descriptor in value.DescriptorArray.Span)
        {
            if (descriptor.Type == BrickType.Dense)
            {
                FreeVoxelArray(descriptor.UserData);
            }
        }

        FreeDescriptorArray(value.DescriptorArray);
    }
}
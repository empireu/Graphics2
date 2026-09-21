using System.Diagnostics;
using System.Runtime.CompilerServices;

// ReSharper disable ConvertToPrimaryConstructor

namespace WinterGame.World.Storage;

public sealed class SectionArena
{
    /// <summary>
    ///     Bytes of descriptor storage per page, from which the array count is derived.
    /// </summary>
    private const int DescriptorPageBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Number of section descriptor arrays one page holds.
    /// </summary>
    private const int DescriptorArraysPerPage = DescriptorPageBytes / (Section.DescriptorsPerSection * sizeof(uint));

    /// <summary>
    ///     Bytes of voxel data per page, from which the array count is derived.
    /// </summary>
    private const int VoxelPageBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Number of one-brick voxel arrays one page holds.
    /// </summary>
    private const int VoxelArraysPerPage = VoxelPageBytes / Section.VoxelsPerBrick;

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

    private readonly List<BrickDescriptor[]> _descriptorPages = [];
    private readonly Stack<DescriptorArray> _freeDescriptorArrays = [];
    private readonly List<byte[]> _voxelPages = [];
    private readonly Stack<VoxelArray> _freeVoxelArrays = [];

#if DEBUG
    private readonly HashSet<DescriptorArray> _descriptorArraysInUse = [];
    private readonly HashSet<VoxelArray> _voxelArraysInUse = [];
#endif

    private readonly Dictionary<ulong, Section> _sections = [];
    
    private void AllocateDescriptorPage()
    {
        Debug.Assert(_descriptorPages.Count < MaxPageNumber, "Tried to allocate more descriptor pages than max page number");
        
        const int capacity = Section.DescriptorsPerSection * DescriptorArraysPerPage;
        
        var page = new BrickDescriptor[capacity];
        var pageIndex = _descriptorPages.Count;
        
        _descriptorPages.Add(page);

        for (var i = capacity - 1; i >= 0; i -= Section.DescriptorsPerSection)
        {
           _freeDescriptorArrays.Push(new DescriptorArray(page, pageIndex, i));
        }
    }
    
    internal DescriptorArray AllocateDescriptorArray()
    {
        if (!_freeDescriptorArrays.TryPop(out var slot))
        {
            AllocateDescriptorPage();

            slot = _freeDescriptorArrays.Pop();
        }

#if DEBUG
        Debug.Assert(_descriptorArraysInUse.Add(slot));
#endif

        return slot;
    }

    internal void FreeDescriptorArray(DescriptorArray array)
    {
#if DEBUG
        Debug.Assert(_descriptorArraysInUse.Remove(array));
#endif
        
        _freeDescriptorArrays.Push(array);
    }

    private void AllocateVoxelPage()
    {
        Debug.Assert(_voxelPages.Count < MaxPageNumber, "Tried to allocate more voxel pages than max page number");
        
        const int capacity = Section.VoxelsPerBrick * VoxelArraysPerPage;
        
        var page = new byte[capacity];
        var pageIndex = _voxelPages.Count;
        
        _voxelPages.Add(page);

        for (var i = capacity - Section.VoxelsPerBrick; i >= 0; i -= Section.VoxelsPerBrick)
        {
            _freeVoxelArrays.Push(new VoxelArray(page, pageIndex, i));
        }
    }

    internal VoxelArray AllocateVoxelArray()
    {
        if (!_freeVoxelArrays.TryPop(out var slot))
        {
            AllocateVoxelPage();

            slot = _freeVoxelArrays.Pop();
        }

#if DEBUG
        Debug.Assert(_voxelArraysInUse.Add(slot));
#endif
        
        return slot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal VoxelArray GetVoxelArray(VoxelArrayHandle handle)
    {
        var page = _voxelPages[handle.PageIndex];
        var result = new VoxelArray(page, handle.PageIndex, handle.Address);
        
#if DEBUG
        Debug.Assert(_voxelArraysInUse.Contains(result));
#endif

        return result;
    }

    internal void FreeVoxelArray(VoxelArrayHandle handle)
    {
        FreeVoxelArray(new VoxelArray(_voxelPages[handle.PageIndex], handle.PageIndex, handle.Address));
    }
    
    internal void FreeVoxelArray(VoxelArray array)
    {
#if DEBUG
        Debug.Assert(_voxelArraysInUse.Remove(array));
#endif
        
        _freeVoxelArrays.Push(array);
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
    
    public void Destroy(SectionPos pos)
    {
        if (!_sections.Remove(pos.GetKey(), out var value))
        {
            throw new InvalidOperationException($"Tried to destroy unknown section at {pos}");
        }

        foreach (var channel in Enum.GetValues<VoxelChannel>())
        {
            for (var i = 0; i < Section.DescriptorsPerChannel; i++)
            {
                value.FreeStorage(channel, i);  
            }
        }
        
        FreeDescriptorArray(value.DescriptorArray);
    }
}
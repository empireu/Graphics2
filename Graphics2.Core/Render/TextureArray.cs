using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

/// <summary>
///     A bindless texture array.
///     Owns a single descriptor set with one <c>SampledImage[capacity]</c> binding.
///     Textures are added at load time and referenced by index from the shader.
///     The registry does not own the textures themselves. It only holds their image views in the descriptor set.
///     A descriptor records the layout its texture is declared to be read in, and the pass open verifies that every registered texture is really in it, because the descriptor is not rewritten afterwards.
///     A texture that a frame writes, as an attachment, cannot be registered until those writes are done for good; a copy destination is allowed, because the flush at the pass open is what makes the declared layout true.
/// </summary>
public sealed unsafe class TextureArray : IDeviceResource
{
    private readonly VkDevice _device;
    private readonly DescriptorPool _pool;
    private readonly VkTexture?[] _textures;
    private readonly ImageLayout[] _recordedLayouts;
    private readonly Stack<uint> _freeIndices;
    private int _disposed;

    /// <summary>
    ///     Maximum number of textures the registry can hold.
    ///     Fixed at creation, and it must match the array size declared in the shader.
    /// </summary>
    public readonly uint Capacity;

    /// <summary>
    ///     Number of textures currently added to the registry.
    /// </summary>
    public uint Count { get; private set; }

    /// <summary>
    ///     How many axes every texture in this registry has, which the shader's declared array has to agree with.
    /// </summary>
    public readonly TextureType Type;

    internal readonly DescriptorSetLayout Layout;

    internal readonly DescriptorSet Set;

    /// <summary>
    ///     The textures currently registered, indexed by the value the shader samples them with. Null where an index is free.
    /// </summary>
    internal ReadOnlySpan<VkTexture?> Textures => _textures;

    private TextureArray(VkDevice device, uint capacity, TextureType type, DescriptorPool pool, DescriptorSetLayout layout, DescriptorSet set)
    {
        _device = device;
        Capacity = capacity;
        Type = type;
        _pool = pool;
        Layout = layout;
        Set = set;
        _textures = new VkTexture?[capacity];
        _recordedLayouts = new ImageLayout[capacity];

        _freeIndices = new Stack<uint>((int)capacity);
        for (var i = (int)capacity - 1; i >= 0; i--)
        {
            _freeIndices.Push((uint)i);
        }
    }

    internal static TextureArray Create(VkDevice device, uint capacity, TextureType type)
    {
        if (capacity == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        var vk = device.Instance.Vk;

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.SampledImage,
            DescriptorCount = capacity
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };

        if (vk.CreateDescriptorPool(device.Device, in poolInfo, null, out var pool) != Result.Success)
        {
            throw new VkException("Failed to create descriptor pool for TextureRegistry.");
        }

        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = capacity,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };

        var bindingFlags = DescriptorBindingFlags.PartiallyBoundBit 
                           | DescriptorBindingFlags.UpdateAfterBindBit
                           | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;

        var bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 1,
            PBindingFlags = &bindingFlags
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &bindingFlagsInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 1,
            PBindings = &binding
        };

        if (vk.CreateDescriptorSetLayout(device.Device, in layoutInfo, null, out var layout) != Result.Success)
        {
            vk.DestroyDescriptorPool(device.Device, pool, null);
            throw new VkException("Failed to create descriptor set layout for TextureRegistry.");
        }

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        if (vk.AllocateDescriptorSets(device.Device, in allocInfo, out var set) != Result.Success)
        {
            vk.DestroyDescriptorSetLayout(device.Device, layout, null);
            vk.DestroyDescriptorPool(device.Device, pool, null);
            throw new VkException("Failed to allocate descriptor set for TextureRegistry.");
        }

        return new TextureArray(device, capacity, type, pool, layout, set);
    }

    /// <summary>
    ///     Adds a texture to the registry and writes its image view into the descriptor set.
    ///     The returned index is what the shader will use to sample this texture (e.g. <c>textures[index].Sample(...)</c>).
    ///     <see cref="layout"/> is the layout the descriptor declares, and it has to be the layout the texture is in or, when a copy into it was recorded this frame, the layout that copy's flush will put it in.
    /// </summary>
    public uint Add(VkTexture texture, TextureLayout layout = TextureLayout.ShaderRead)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (texture == null)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        if (_freeIndices.Count == 0)
        {
            throw new InvalidOperationException("TextureRegistry is full.");
        }

        if (texture.Type != Type)
        {
            throw new ArgumentException(
                $"Texture is a {texture.Type} and this registry holds {Type} textures, so the shader's declared array cannot sample it.",
                nameof(texture)
            );
        }

        var recordedLayout = layout.ToVk();

        if (texture.CurrentLayout != recordedLayout && texture.CurrentLayout != ImageLayout.TransferDstOptimal)
        {
            throw new InvalidOperationException(
                $"Texture is in {texture.CurrentLayout} and cannot be registered as {recordedLayout}. Register it in the layout its descriptor declares, or in the same frame as a copy into it, whose flush at the pass open is what establishes that layout."
            );
        }

        var index = _freeIndices.Pop();
        WriteDescriptor(index, texture, recordedLayout);
        _textures[index] = texture;
        _recordedLayouts[index] = recordedLayout;
        Count++;
        return index;
    }

    /// <summary>
    ///     Removes a texture from the registry and returns its index to the free pool.
    ///     The index may be reused by a subsequent <see cref="Add"/> calls.
    ///     Does not destroy the texture itself.
    /// </summary>
    public void Remove(uint index)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (index >= Capacity || _textures[index] == null)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Index is not in use.");
        }

        _textures[index] = null;
        _recordedLayouts[index] = ImageLayout.Undefined;
        _freeIndices.Push(index);
        Count--;
    }

    /// <summary>
    ///     Checks that every registered texture is still in the layout its descriptor declares, which the binding needs because the descriptor is written once, at add time.
    ///     A texture the frame has since used as an attachment reports its new layout here rather than sampling with a false layout; a copy destination is in its declared layout by now, because the flush ran just before this.
    /// </summary>
    internal void ValidateRecordedLayouts(string parameterName)
    {
        for (var index = 0; index < _textures.Length; index++)
        {
            var texture = _textures[index];

            if (texture == null || texture.CurrentLayout == _recordedLayouts[index])
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Texture array element {index} bound to \"{parameterName}\" is in {texture.CurrentLayout}, but its descriptor records {_recordedLayouts[index]}. A texture registered here cannot be used as an attachment or as a copy destination while it stays registered."
            );
        }
    }

    private void WriteDescriptor(uint index, VkTexture texture, ImageLayout layout)
    {
        var imageInfo = new DescriptorImageInfo
        {
            Sampler = default,
            ImageView = texture.View,
            ImageLayout = layout
        };

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = 0,
            DstArrayElement = index,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = &imageInfo
        };

        _device.Instance.Vk.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
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

        vk.DestroyDescriptorPool(_device.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Device, Layout, null);
    }
}

using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

/// <summary>
///     A bindless texture array.
///     Owns a single descriptor set with one <c>SampledImage[capacity]</c> binding.
///     Textures are added at load time and referenced by index from the shader.
///     The registry does not own the textures themselves. It only holds their image views in the descriptor set.
///     A texture has to be in <see cref="ImageLayout.ShaderReadOnlyOptimal" /> when it is added and has to stay there while it is registered, because the descriptor records that layout once and is not rewritten afterwards.
///     A texture that a frame writes, as an attachment or as a copy destination, cannot be registered until those writes are done for good.
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

    internal readonly DescriptorSetLayout Layout;

    internal readonly DescriptorSet Set;

    /// <summary>
    ///     The textures currently registered, indexed by the value the shader samples them with. Null where an index is free.
    /// </summary>
    internal ReadOnlySpan<VkTexture?> Textures => _textures;

    private TextureArray(VkDevice device, uint capacity, DescriptorPool pool, DescriptorSetLayout layout, DescriptorSet set)
    {
        _device = device;
        Capacity = capacity;
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

    internal static TextureArray Create(VkDevice device, uint capacity)
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

        return new TextureArray(device, capacity, pool, layout, set);
    }

    /// <summary>
    ///     Adds a texture to the registry and writes its image view into the descriptor set.
    ///     The returned index is what the shader will use to sample this texture (e.g. <c>textures[index].Sample(...)</c>).
    /// </summary>
    public uint Add(VkTexture texture)
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

        if (texture.CurrentLayout != ImageLayout.ShaderReadOnlyOptimal)
        {
            throw new InvalidOperationException(
                $"Texture is in {texture.CurrentLayout} and cannot be registered. It has to be in {ImageLayout.ShaderReadOnlyOptimal} when it is added, because the descriptor records that layout once and is never rewritten. Upload it with UpdateTexture, or register it after a render pass has begun so the read transition after a copy has already been emitted."
            );
        }

        var index = _freeIndices.Pop();
        WriteDescriptor(index, texture);
        _textures[index] = texture;
        _recordedLayouts[index] = texture.CurrentLayout;
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
    ///     Checks that every registered texture is still in the layout its descriptor records, which the binding needs because the descriptor is written once, at add time.
    ///     A texture the frame has since used as an attachment or as a copy destination reports its new layout here rather than sampling with a false layout.
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

    private void WriteDescriptor(uint index, VkTexture texture)
    {
        var imageInfo = new DescriptorImageInfo
        {
            Sampler = default,
            ImageView = texture.View,
            ImageLayout = texture.CurrentLayout
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

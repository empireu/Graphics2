using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;
using Silk.NET.Vulkan;
using Silk.NET.Core.Native;

// ReSharper disable ForCanBeConvertedToForeach

namespace Graphics2.Core.Resources;

public readonly struct AllocatorStats
{
    public int Pages { get; init; }
    
    public long BytesUsed { get; init; }
    
    public int FreeRanges { get; init; }
}

public enum AllocatorFamily
{
    GpuOnly,
    CpuToGpu,
    GpuToCpu
}

public sealed unsafe class VulkanResourceFactory : IDisposable
{
    private const long DefaultBlockSize = 256 * 1024 * 1024;

    private readonly VkDevice _device;
    private readonly BlockAllocator<VulkanMemory> _gpuOnly;
    private readonly BlockAllocator<VulkanMemory> _cpuToGpu;
    private readonly BlockAllocator<VulkanMemory> _gpuToCpu;
    private readonly ShaderCompiler _shaderCompiler;
    private readonly CommandPool _transferPool;
    private int _disposed;

    public VulkanResourceFactory(VkDevice device)
    {
        _device = device;

        var gpuOnlyLayer = VulkanAllocationLayer.Create(device, MemoryUsage.GpuOnly);
        var cpuToGpuLayer = VulkanAllocationLayer.Create(device, MemoryUsage.CpuToGpu);
        var gpuToCpuLayer = VulkanAllocationLayer.Create(device, MemoryUsage.GpuToCpu);

        _gpuOnly = new BlockAllocator<VulkanMemory>(DefaultBlockSize, gpuOnlyLayer);
        _cpuToGpu = new BlockAllocator<VulkanMemory>(DefaultBlockSize, cpuToGpuLayer);
        _gpuToCpu = new BlockAllocator<VulkanMemory>(DefaultBlockSize, gpuToCpuLayer);
        _shaderCompiler = new ShaderCompiler();

        var vk = device.Instance.Vk;
        var poolCreateInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = device.Info.ChosenGraphicsPresentFamily
        };

        if (vk.CreateCommandPool(device.Device, in poolCreateInfo, null, out _transferPool) != Result.Success)
        {
            throw new VkAllocationException("Failed to create transfer command pool.");
        }
    }

    private BlockAllocator<VulkanMemory> GetAllocator(AllocatorFamily family) => family switch
    {
        AllocatorFamily.GpuOnly => _gpuOnly,
        AllocatorFamily.CpuToGpu => _cpuToGpu,
        AllocatorFamily.GpuToCpu => _gpuToCpu,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, $"Invalid allocator family {family}")
    };

    public AllocatorStats GetAllocatorStats(AllocatorFamily family)
    {
        var allocator = GetAllocator(family);
        
        var usedBytes = 0L;
        var freeRanges = 0;
        for (var index = 0; index < allocator.Blocks.Count; index++)
        {
            var allocatorBlock = allocator.Blocks[index];

            usedBytes += allocatorBlock.UsedBytes;
            freeRanges += allocatorBlock.FreeRangeCount;
        }

        return new AllocatorStats
        {
            Pages = allocator.Blocks.Count,
            BytesUsed = usedBytes,
            FreeRanges = freeRanges
        };
    }
    
    /// <summary>
    ///     Creates a device-local vertex and index buffer.
    ///     Both capacities are floors applied to the first allocation, and what is actually allocated is rounded up to a power of two.
    /// </summary>
    public Mesh<TVertex> CreateMesh<TVertex>(int vertexCapacity = 0, int indexCapacity = 0) where TVertex : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(indexCapacity);

        return new Mesh<TVertex>(_device, this, vertexCapacity, indexCapacity);
    }

    public VkBuffer CreateBuffer(BufferDescription description)
    {
        if (description.Size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "Size must be positive.");
        }

        var vk = _device.Instance.Vk;

        var vkUsage = BufferUsageToVk(description.Usage);
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)description.Size,
            Usage = vkUsage,
            SharingMode = SharingMode.Exclusive
        };

        if (vk.CreateBuffer(_device.Device, in createInfo, null, out var bufferHandle) != Result.Success)
        {
            throw new VkAllocationException("Failed to create buffer.");
        }

        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2
        };
        
        var memoryDedicatedInfo = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements
        };
        
        requirements.PNext = &memoryDedicatedInfo;

        var bufferMemoryInfo = new BufferMemoryRequirementsInfo2
        {
            SType = StructureType.BufferMemoryRequirementsInfo2,
            Buffer = bufferHandle
        };
        
        vk.GetBufferMemoryRequirements2(_device.Device, in bufferMemoryInfo, &requirements);

        var allocator = BufferUsageToAllocator(description.Usage);
        var allocation = allocator.Allocate((long)requirements.MemoryRequirements.Size, (long)requirements.MemoryRequirements.Alignment);
        var memory = allocator.GetBase(allocation);

        if (vk.BindBufferMemory(_device.Device, bufferHandle, memory.Handle, (ulong)allocation.Offset) != Result.Success)
        {
            allocator.Free(allocation);
            vk.DestroyBuffer(_device.Device, bufferHandle, null);
            throw new VkAllocationException("Failed to bind buffer memory.");
        }

        return new VkBuffer(_device, allocator, bufferHandle, allocation, memory, description.Size, BufferUsageToMemoryUsage(description.Usage), description.Usage);
    }

    public VkShader CreateShader(ShaderDescription description)
    {
        var vk = _device.Instance.Vk;

        fixed (byte* spirV = description.SpirV.Span)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)description.SpirV.Length,
                PCode = (uint*)spirV
            };

            if (vk.CreateShaderModule(_device.Device, in createInfo, null, out var shaderModule) != Result.Success)
            {
                throw new VkAllocationException("Failed to create shader module.");
            }

            return new VkShader(_device, shaderModule, description.Stage, description.EntryPoint);
        }
    }

    public Texture CreateTexture(TextureDescription description)
    {
        description.Validate();

        var vk = _device.Instance.Vk;
        var format = TextureFormatToVk(description.Format);
        var usage = TextureUsageToVk(description.Usage);

        // A 3D image is one layer of depth slices, so its array layer count is always 1 and its third extent carries the depth:
        var isVolume = description.Type == TextureType.Texture3D;
        var arrayLayers = isVolume ? 1u : description.ArrayLayers;
        var viewType = isVolume
            ? ImageViewType.Type3D
            : arrayLayers > 1
                ? ImageViewType.Type2DArray
                : ImageViewType.Type2D;

        var createInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = isVolume ? ImageType.Type3D : ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(description.Width, description.Height, description.Depth),
            MipLevels = description.MipLevels,
            ArrayLayers = arrayLayers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        if (vk.CreateImage(_device.Device, in createInfo, null, out var imageHandle) != Result.Success)
        {
            throw new VkAllocationException("Failed to create image.");
        }

        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2
        };

        var memoryDedicatedInfo = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements
        };

        requirements.PNext = &memoryDedicatedInfo;

        var imageMemoryInfo = new ImageMemoryRequirementsInfo2
        {
            SType = StructureType.ImageMemoryRequirementsInfo2,
            Image = imageHandle
        };

        vk.GetImageMemoryRequirements2(_device.Device, in imageMemoryInfo, &requirements);

        var allocation = _gpuOnly.Allocate((long)requirements.MemoryRequirements.Size, (long)requirements.MemoryRequirements.Alignment);
        var memory = _gpuOnly.GetBase(allocation);

        if (vk.BindImageMemory(_device.Device, imageHandle, memory.Handle, (ulong)allocation.Offset) != Result.Success)
        {
            _gpuOnly.Free(allocation);
            vk.DestroyImage(_device.Device, imageHandle, null);
            throw new VkAllocationException("Failed to bind image memory.");
        }

        var viewCreateInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = imageHandle,
            ViewType = viewType,
            Format = format,
            Components = new ComponentMapping
            {
                R = ComponentSwizzle.Identity,
                G = ComponentSwizzle.Identity,
                B = ComponentSwizzle.Identity,
                A = ComponentSwizzle.Identity
            },
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = FormatToAspectMask(format),
                BaseMipLevel = 0,
                LevelCount = description.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = arrayLayers
            }
        };

        if (vk.CreateImageView(_device.Device, in viewCreateInfo, null, out var viewHandle) != Result.Success)
        {
            _gpuOnly.Free(allocation);
            vk.DestroyImage(_device.Device, imageHandle, null);
            throw new VkAllocationException("Failed to create image view.");
        }

        return new Texture(_device, _gpuOnly, imageHandle, viewHandle, allocation, description.Width, description.Height, description.Depth, description.Format, description.Usage, description.Type, FormatToAspectMask(format));
    }

    public VkSampler CreateSampler(SamplerDescription description)
    {
        return VkSampler.Create(_device, description);
    }

    /// <summary>
    ///     Creates a bindless texture registry with the given capacity.
    ///     The capacity must match the array size declared in the shader (e.g. <c>Texture2D textures[1024]</c> requires capacity 1024), and every texture added to it has to have the registered <paramref name="type" />.
    /// </summary>
    public TextureArray CreateTextureArray(uint capacity, TextureType type = TextureType.Texture2D)
    {
        return TextureArray.Create(_device, capacity, type);
    }

    public Pipeline CreatePipeline(PipelineDescription description)
    {
        var compiled = _shaderCompiler.Compile(description.Source, description.VertexEntryPoint, description.FragmentEntryPoint);

        var vertexShader = CreateShader(new ShaderDescription
        {
            SpirV = compiled.VertexSpirV,
            Stage = ShaderStage.Vertex,
            EntryPoint = "main"
        });

        VkShader? fragmentShader = null;
        if (compiled.FragmentSpirV != null && description.FragmentEntryPoint != null)
        {
            fragmentShader = CreateShader(new ShaderDescription
            {
                SpirV = compiled.FragmentSpirV,
                Stage = ShaderStage.Fragment,
                EntryPoint = "main"
            });
        }

        var descriptorSetLayouts = CreateDescriptorSetLayouts(compiled.Reflection);
        var (pushConstantSize, pushConstantStageFlags) = ValidatePushConstants(compiled.Reflection, description.PushConstantLayout);
        ValidateSsbos(compiled.Reflection, description.SsboLayouts);
        var pipelineLayout = CreatePipelineLayout(descriptorSetLayouts, pushConstantSize, pushConstantStageFlags);
        var pipelineHandle = CreateGraphicsPipeline(description, vertexShader, fragmentShader, pipelineLayout, compiled.Reflection);

        return new Pipeline(_device, pipelineHandle, pipelineLayout, descriptorSetLayouts, vertexShader, fragmentShader, compiled.Reflection, pushConstantSize, pushConstantStageFlags);
    }
    
    // Temporary location for these methods

    public void UpdateBuffer(VkBuffer dst, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (data.Length > dst.Size)
        {
            throw new ArgumentException($"Data ({data.Length} bytes) exceeds buffer size ({dst.Size} bytes).", nameof(data));
        }

        var staging = CreateBuffer(new BufferDescription
        {
            Size = data.Length,
            Usage = BufferUsage.Staging
        });

        data.CopyTo(staging.MappedData);

        var vk = _device.Instance.Vk;
        var cmd = BeginOneTimeCommand();

        var region = new BufferCopy
        {
            SrcOffset = 0,
            DstOffset = 0,
            Size = (ulong)data.Length
        };

        vk.CmdCopyBuffer(cmd, staging.Handle, dst.Handle, 1, &region);

        EndOneTimeCommand(cmd);

        staging.Dispose();
    }

    public void UpdateTexture(Texture dst, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        var pixelSize = dst.Format.TexelSize();
        var expectedSize = (long)dst.Width * dst.Height * dst.Depth * pixelSize;
        if (data.Length != expectedSize)
        {
            throw new ArgumentException($"Data ({data.Length} bytes) does not match texture size ({expectedSize} bytes: {dst.Width}x{dst.Height}x{dst.Depth}x{pixelSize}).", nameof(data));
        }

        var staging = CreateBuffer(new BufferDescription
        {
            Size = data.Length,
            Usage = BufferUsage.Staging
        });

        data.CopyTo(staging.MappedData);

        var vk = _device.Instance.Vk;
        var cmd = BeginOneTimeCommand();

        var aspectMask = FormatToAspectMask(TextureFormatToVk(dst.Format));

        if (dst.CurrentLayout != ImageLayout.TransferDstOptimal)
        {
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.None,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.CopyBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = dst.CurrentLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = dst.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = aspectMask,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(dst.Width, dst.Height, dst.Depth)
        };

        vk.CmdCopyBufferToImage(cmd, staging.Handle, dst.Image, ImageLayout.TransferDstOptimal, 1, &region);

        {
            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.AllGraphicsBit,
                DstAccessMask = AccessFlags2.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = dst.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            vk.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        dst.CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;

        EndOneTimeCommand(cmd);

        staging.Dispose();
    }

    private CommandBuffer BeginOneTimeCommand()
    {
        var vk = _device.Instance.Vk;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _transferPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        if (vk.AllocateCommandBuffers(_device.Device, in allocInfo, out var cmd) != Result.Success)
        {
            throw new VkAllocationException("Failed to allocate one-time command buffer.");
        }

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        if (vk.BeginCommandBuffer(cmd, in beginInfo) != Result.Success)
        {
            vk.FreeCommandBuffers(_device.Device, _transferPool, 1, &cmd);
            throw new VkAllocationException("Failed to begin one-time command buffer.");
        }

        return cmd;
    }

    private void EndOneTimeCommand(CommandBuffer cmd)
    {
        var vk = _device.Instance.Vk;

        if (vk.EndCommandBuffer(cmd) != Result.Success)
        {
            vk.FreeCommandBuffers(_device.Device, _transferPool, 1, &cmd);
            throw new VkAllocationException("Failed to end one-time command buffer.");
        }

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };

        if (vk.QueueSubmit(_device.Queue, 1, &submitInfo, default) != Result.Success)
        {
            vk.FreeCommandBuffers(_device.Device, _transferPool, 1, &cmd);
            throw new VkAllocationException("Failed to submit one-time command buffer.");
        }

        vk.QueueWaitIdle(_device.Queue);

        vk.FreeCommandBuffers(_device.Device, _transferPool, 1, &cmd);
    }

    private DescriptorSetLayout[] CreateDescriptorSetLayouts(ShaderReflection reflection)
    {
        var vk = _device.Instance.Vk;

        if (reflection.Parameters.Length == 0)
        {
            return [];
        }

        var maxSet = 0u;
        foreach (var param in reflection.Parameters)
        {
            if (param.BindingSpace > maxSet)
            {
                maxSet = param.BindingSpace;
            }
        }

        var setCount = maxSet + 1;
        var bindingsBySet = new List<DescriptorSetLayoutBinding>[(int)setCount];
        var bindingFlagsBySet = new List<DescriptorBindingFlags>[(int)setCount];
        for (var i = 0; i < setCount; i++)
        {
            bindingsBySet[i] = [];
            bindingFlagsBySet[i] = [];
        }

        foreach (var param in reflection.Parameters)
        {
            var descriptorType = param.Kind switch
            {
                ShaderParameterKind.UniformBuffer => DescriptorType.UniformBuffer,
                ShaderParameterKind.StorageBuffer => DescriptorType.StorageBuffer,
                ShaderParameterKind.SampledImage => DescriptorType.SampledImage,
                ShaderParameterKind.Sampler => DescriptorType.Sampler,
                ShaderParameterKind.StorageImage => DescriptorType.StorageImage,
                _ => throw new NotSupportedException($"Unsupported shader parameter kind: {param.Kind}")
            };

            if (param.ElementCount == 0)
            {
                throw new NotSupportedException($"Shader parameter \"{param.Name}\" is an unsized array. Use a fixed-size array (e.g. textures[1024]) for bindless resources.");
            }

            var isBindless = param.ElementCount > 1;
            var descriptorCount = isBindless ? param.ElementCount : 1u;
            var flags = isBindless
                ? DescriptorBindingFlags.PartiallyBoundBit
                  | DescriptorBindingFlags.UpdateAfterBindBit
                  | DescriptorBindingFlags.UpdateUnusedWhilePendingBit
                : DescriptorBindingFlags.None;

            bindingsBySet[(int)param.BindingSpace].Add(new DescriptorSetLayoutBinding
            {
                Binding = param.BindingIndex,
                DescriptorType = descriptorType,
                DescriptorCount = descriptorCount,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                PImmutableSamplers = null
            });
            
            bindingFlagsBySet[(int)param.BindingSpace].Add(flags);
        }

        var layouts = new DescriptorSetLayout[setCount];
        for (var i = 0; i < setCount; i++)
        {
            var bindings = bindingsBySet[i];
            if (bindings.Count == 0)
            {
                layouts[i] = default;
                continue;
            }

            var bindingsArray = bindings.ToArray();
            var flagsArray = bindingFlagsBySet[i].ToArray();

            var hasBindless = false;
            for (var j = 0; j < flagsArray.Length; j++)
            {
                if (flagsArray[j] != DescriptorBindingFlags.None)
                {
                    hasBindless = true;
                    break;
                }
            }

            fixed (DescriptorSetLayoutBinding* ptr = bindingsArray)
            fixed (DescriptorBindingFlags* flagsPtr = flagsArray)
            {
                DescriptorSetLayoutBindingFlagsCreateInfo bindingFlagsInfo = default;
                void* pNext = null;

                if (hasBindless)
                {
                    bindingFlagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                        BindingCount = (uint)flagsArray.Length,
                        PBindingFlags = flagsPtr
                    };
                    pNext = &bindingFlagsInfo;
                }

                var createInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    PNext = pNext,
                    Flags = hasBindless ? DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit : DescriptorSetLayoutCreateFlags.None,
                    BindingCount = (uint)bindingsArray.Length,
                    PBindings = ptr
                };

                if (vk.CreateDescriptorSetLayout(_device.Device, in createInfo, null, out layouts[i]) != Result.Success)
                {
                    throw new VkAllocationException($"Failed to create descriptor set layout for set {i}.");
                }
            }
        }

        return layouts;
    }

    private PipelineLayout CreatePipelineLayout(DescriptorSetLayout[] descriptorSetLayouts, uint pushConstantSize, ShaderStageFlags pushConstantStageFlags)
    {
        var vk = _device.Instance.Vk;

        PushConstantRange[] pushConstantRanges = null!;
        var pushConstantRangeCount = 0u;

        if (pushConstantSize > 0)
        {
            pushConstantRanges =
            [
                new PushConstantRange
                {
                    StageFlags = pushConstantStageFlags,
                    Offset = 0,
                    Size = pushConstantSize
                }
            ];
            pushConstantRangeCount = 1;
        }

        fixed (DescriptorSetLayout* ptr = descriptorSetLayouts)
        fixed (PushConstantRange* pcPtr = pushConstantRanges)
        {
            var createInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = ptr,
                PushConstantRangeCount = pushConstantRangeCount,
                PPushConstantRanges = pcPtr
            };

            if (vk.CreatePipelineLayout(_device.Device, in createInfo, null, out var layout) != Result.Success)
            {
                throw new VkAllocationException("Failed to create pipeline layout.");
            }

            return layout;
        }
    }

    private static (uint size, ShaderStageFlags stageFlags) ValidatePushConstants(ShaderReflection reflection, PushConstantLayout? appLayout)
    {
        if (reflection.PushConstants.Length == 0)
        {
            if (appLayout != null)
            {
                throw new VkAllocationException("Pipeline description provides a PushConstantLayout but the shader declares no push constants.");
            }

            return (0, ShaderStageFlags.None);
        }

        if (reflection.PushConstants.Length > 1)
        {
            throw new VkAllocationException($"Multiple push constant blocks are not supported. Shader declares {reflection.PushConstants.Length}.");
        }

        if (appLayout == null)
        {
            throw new VkAllocationException("Shader declares push constants but PipelineDescription has no PushConstantLayout.");
        }

        var shaderPc = reflection.PushConstants[0];

        if (appLayout.Size != shaderPc.Size)
        {
            throw new VkAllocationException($"Push constant size mismatch: app says {appLayout.Size} bytes, shader says {shaderPc.Size} bytes.");
        }

        if (appLayout.Fields.Length != shaderPc.Fields.Length)
        {
            throw new VkAllocationException($"Push constant field count mismatch: app says {appLayout.Fields.Length}, shader says {shaderPc.Fields.Length}.");
        }

        for (var i = 0; i < appLayout.Fields.Length; i++)
        {
            var appField = appLayout.Fields[i];
            var found = false;

            for (var j = 0; j < shaderPc.Fields.Length; j++)
            {
                if (string.Equals(shaderPc.Fields[j].Name, appField.Name, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;

                    if (shaderPc.Fields[j].Offset != appField.Offset)
                    {
                        throw new VkAllocationException($"Push constant field \"{appField.Name}\" offset mismatch: app says {appField.Offset}, shader says {shaderPc.Fields[j].Offset}.");
                    }

                    break;
                }
            }

            if (!found)
            {
                throw new VkAllocationException($"Push constant field \"{appField.Name}\" not found in shader reflection.");
            }
        }

        return (shaderPc.Size, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
    }

    private static void ValidateSsbos(ShaderReflection reflection, Dictionary<string, SsboLayout>? ssboLayouts)
    {
        if (ssboLayouts == null)
        {
            return;
        }

        foreach (var param in reflection.Parameters)
        {
            if (param.Kind != ShaderParameterKind.StorageBuffer)
            {
                continue;
            }

            if (!ssboLayouts.TryGetValue(param.Name, out var appLayout))
            {
                continue;
            }

            if (appLayout.Stride != param.ElementStride)
            {
                throw new VkAllocationException($"SSBO \"{param.Name}\" element stride mismatch: app says {appLayout.Stride} bytes, shader says {param.ElementStride} bytes.");
            }

            if (param.ElementFields == null || param.ElementFields.Length == 0)
            {
                continue;
            }

            foreach (var appField in appLayout.Fields)
            {
                var found = false;

                for (var j = 0; j < param.ElementFields.Length; j++)
                {
                    if (string.Equals(param.ElementFields[j].Name, appField.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;

                        if (param.ElementFields[j].Offset != appField.Offset)
                        {
                            throw new VkAllocationException($"SSBO \"{param.Name}\" field \"{appField.Name}\" offset mismatch: app says {appField.Offset}, shader says {param.ElementFields[j].Offset}.");
                        }

                        break;
                    }
                }

                if (!found)
                {
                    throw new VkAllocationException($"SSBO \"{param.Name}\" field \"{appField.Name}\" not found in shader reflection.");
                }
            }
        }
    }

    private Silk.NET.Vulkan.Pipeline CreateGraphicsPipeline(
        PipelineDescription description,
        VkShader vertexShader,
        VkShader? fragmentShader,
        PipelineLayout pipelineLayout,
        ShaderReflection reflection)
    {
        if (description.Wireframe && !_device.Info.FillModeNonSolidEnabled)
        {
            throw new VkException($"Cannot use wireframe pipeline without {nameof(VkDeviceDescription.FillModeNonSolid)}");
        }
        
        var vk = _device.Instance.Vk;

        var stageCount = fragmentShader != null ? 2u : 1u;
        var stages = stackalloc PipelineShaderStageCreateInfo[(int)stageCount];

        var vertName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexShader.Handle,
            PName = (byte*)vertName
        };

        nint fragName = 0;
        if (fragmentShader != null)
        {
            fragName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentShader.Handle,
                PName = (byte*)fragName
            };
        }

        var vsEntry = Array.Find(reflection.EntryPoints, ep => ep.Stage == ShaderStage.Vertex);
        var shaderVertexInputs = vsEntry.VertexInputs;

        VertexInputBindingDescription bindingDescription;
        VertexInputAttributeDescription[] attributeDescriptions;

        if (description.VertexLayout != null)
        {
            var layout = description.VertexLayout;
            bindingDescription = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = layout.Stride,
                InputRate = VertexInputRate.Vertex
            };

            attributeDescriptions = new VertexInputAttributeDescription[layout.Attributes.Length];

            for (var i = 0; i < layout.Attributes.Length; i++)
            {
                var appAttr = layout.Attributes[i];

                var found = false;
                var shaderFormat = VertexAttributeFormat.Float;
                var location = 0u;

                for (var j = 0; j < shaderVertexInputs.Length; j++)
                {
                    if (string.Equals(shaderVertexInputs[j].Name, appAttr.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        shaderFormat = shaderVertexInputs[j].Format;
                        location = shaderVertexInputs[j].Location;
                        break;
                    }
                }

                if (!found)
                {
                    throw new VkAllocationException($"Vertex layout attribute \"{appAttr.Name}\" not found in shader reflection.");
                }

                if (shaderFormat != appAttr.Format && !IsCompatibleFormat(shaderFormat, appAttr.Format))
                {
                    throw new VkAllocationException($"Vertex layout attribute \"{appAttr.Name}\" format mismatch: app says {appAttr.Format}, shader says {shaderFormat}.");
                }

                attributeDescriptions[i] = new VertexInputAttributeDescription
                {
                    Binding = 0,
                    Location = location,
                    Format = VertexAttributeFormatToVk(appAttr.Format),
                    Offset = appAttr.Offset
                };
            }
        }
        else
        {
            if (shaderVertexInputs.Length == 0)
            {
                bindingDescription = default;
                attributeDescriptions = [];
            }
            else
            {
                bindingDescription = new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = 0,
                    InputRate = VertexInputRate.Vertex
                };

                attributeDescriptions = new VertexInputAttributeDescription[shaderVertexInputs.Length];
                for (var i = 0; i < shaderVertexInputs.Length; i++)
                {
                    attributeDescriptions[i] = new VertexInputAttributeDescription
                    {
                        Binding = 0,
                        Location = shaderVertexInputs[i].Location,
                        Format = VertexAttributeFormatToVk(shaderVertexInputs[i].Format),
                        Offset = 0
                    };
                }
            }
        }

        var hasVertexBindings = description.VertexLayout != null || shaderVertexInputs.Length > 0;

        fixed (VertexInputAttributeDescription* attributeDescriptionsPtr = attributeDescriptions)
        {
            var vertexInputInfo = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = hasVertexBindings ? 1u : 0u,
                PVertexBindingDescriptions = hasVertexBindings ? &bindingDescription : null,
                VertexAttributeDescriptionCount = (uint)attributeDescriptions.Length,
                PVertexAttributeDescriptions = attributeDescriptions.Length > 0 ? attributeDescriptionsPtr : null
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopologyToVk(description.Topology),
                PrimitiveRestartEnable = false
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };

            var lineWidth = _device.Info.WideLinesEnabled
                ? Math.Clamp(description.LineWidth, _device.Info.LineWidthRange[0], _device.Info.LineWidthRange[1])
                : 1.0f;

            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = description.Wireframe ? PolygonMode.Line : PolygonMode.Fill,
                CullMode = CullModeToVk(description.CullMode),
                FrontFace = FrontFaceToVk(description.FrontFace),
                DepthBiasEnable = description.DepthBiasConstant != 0.0f || description.DepthBiasSlope != 0.0f,
                DepthBiasConstantFactor = description.DepthBiasConstant,
                DepthBiasSlopeFactor = description.DepthBiasSlope,
                DepthBiasClamp = 0.0f,
                LineWidth = lineWidth
            };

            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
                SampleShadingEnable = false
            };

            var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[description.ColorAttachments.Length];
            for (var i = 0; i < description.ColorAttachments.Length; i++)
            {
                var blend = description.Blends != null && i < description.Blends.Length
                    ? description.Blends[i]
                    : BlendState.None;

                colorBlendAttachments[i] = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = blend.Enabled,
                    SrcColorBlendFactor = BlendFactorToVk(blend.SrcFactor),
                    DstColorBlendFactor = BlendFactorToVk(blend.DstFactor),
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactorToVk(blend.SrcFactor),
                    DstAlphaBlendFactor = BlendFactorToVk(blend.DstFactor),
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };
            }

            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                LogicOp = LogicOp.Copy,
                AttachmentCount = (uint)description.ColorAttachments.Length,
                PAttachments = colorBlendAttachments
            };

            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = description.DepthTest,
                DepthWriteEnable = description.DepthWrite,
                DepthCompareOp = CompareOpToVk(description.DepthCompare),
                DepthBoundsTestEnable = false,
                StencilTestEnable = false
            };

            var dynamicStates = stackalloc DynamicState[2]
            {
                DynamicState.Viewport,
                DynamicState.Scissor
            };

            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            var colorFormats = stackalloc Format[description.ColorAttachments.Length];
            for (var i = 0; i < description.ColorAttachments.Length; i++)
            {
                colorFormats[i] = TextureFormatToVk(description.ColorAttachments[i]);
            }

            var renderingInfo = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = (uint)description.ColorAttachments.Length,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = description.DepthAttachment != null
                    ? TextureFormatToVk(description.DepthAttachment.Value)
                    : Format.Undefined
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PColorBlendState = &colorBlending,
                PDepthStencilState = &depthStencil,
                PDynamicState = &dynamicState,
                Layout = pipelineLayout,
                RenderPass = default,
                PNext = &renderingInfo
            };

            Silk.NET.Vulkan.Pipeline pipelineHandle;
            var result = vk.CreateGraphicsPipelines(_device.Device, default, 1, in pipelineInfo, null, out pipelineHandle);

            SilkMarshal.Free(vertName);
            if (fragName != 0)
            {
                SilkMarshal.Free(fragName);
            }

            if (result != Result.Success)
            {
                throw new VkAllocationException("Failed to create graphics pipeline.");
            }

            return pipelineHandle;
        }
    }

    private BlockAllocator<VulkanMemory> BufferUsageToAllocator(BufferUsage usage)
    {
        var memoryUsage = BufferUsageToMemoryUsage(usage);
        return memoryUsage switch
        {
            MemoryUsage.GpuOnly => _gpuOnly,
            MemoryUsage.CpuToGpu => _cpuToGpu,
            MemoryUsage.GpuToCpu => _gpuToCpu,
            _ => throw new ArgumentOutOfRangeException(nameof(usage))
        };
    }

    private static MemoryUsage BufferUsageToMemoryUsage(BufferUsage usage)
    {
        if (usage.HasFlag(BufferUsage.Staging) || usage.HasFlag(BufferUsage.Uniform) || usage.HasFlag(BufferUsage.IndirectHostVisible))
        {
            return MemoryUsage.CpuToGpu;
        }

        return MemoryUsage.GpuOnly;
    }

    private static BufferUsageFlags BufferUsageToVk(BufferUsage usage)
    {
        var flags = BufferUsageFlags.None;

        if (usage.HasFlag(BufferUsage.Vertex))
        {
            flags |= BufferUsageFlags.VertexBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Index))
        {
            flags |= BufferUsageFlags.IndexBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Uniform))
        {
            flags |= BufferUsageFlags.UniformBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Storage))
        {
            flags |= BufferUsageFlags.StorageBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Staging))
        {
            flags |= BufferUsageFlags.TransferSrcBit;
        }
        
        if (usage.HasFlag(BufferUsage.Indirect))
        {
            flags |= BufferUsageFlags.IndirectBufferBit;
        }

        if (usage.HasFlag(BufferUsage.IndirectHostVisible))
        {
            flags |= BufferUsageFlags.IndirectBufferBit;
        }

        if (usage.HasFlag(BufferUsage.TransferDestination))
        {
            flags |= BufferUsageFlags.TransferDstBit;
        }

        return flags;
    }

    private static Format TextureFormatToVk(TextureFormat format) => format switch
    {
        TextureFormat.R8Unorm => Format.R8Unorm,
        TextureFormat.R8Uint => Format.R8Uint,
        TextureFormat.R8G8B8A8Unorm => Format.R8G8B8A8Unorm,
        TextureFormat.R8G8B8A8Srgb => Format.R8G8B8A8Srgb,
        TextureFormat.B8G8R8A8Srgb => Format.B8G8R8A8Srgb,
        TextureFormat.R32G32B32A32Sfloat => Format.R32G32B32A32Sfloat,
        TextureFormat.D32Sfloat => Format.D32Sfloat,
        TextureFormat.D24UnormS8Uint => Format.D24UnormS8Uint,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static ImageUsageFlags TextureUsageToVk(TextureUsage usage)
    {
        var flags = ImageUsageFlags.None;

        if (usage.HasFlag(TextureUsage.Sampled))
        {
            flags |= ImageUsageFlags.SampledBit;
        }

        if (usage.HasFlag(TextureUsage.ColorAttachment))
        {
            flags |= ImageUsageFlags.ColorAttachmentBit;
        }

        if (usage.HasFlag(TextureUsage.DepthAttachment))
        {
            flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if (usage.HasFlag(TextureUsage.Storage))
        {
            flags |= ImageUsageFlags.StorageBit;
        }

        if (usage.HasFlag(TextureUsage.TransferSource))
        {
            flags |= ImageUsageFlags.TransferSrcBit;
        }

        if (usage.HasFlag(TextureUsage.TransferDestination))
        {
            flags |= ImageUsageFlags.TransferDstBit;
        }

        return flags;
    }

    private static ImageAspectFlags FormatToAspectMask(Format format) => format switch
    {
        Format.D32Sfloat => ImageAspectFlags.DepthBit,
        Format.D24UnormS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.ColorBit
    };

    private static bool IsCompatibleFormat(VertexAttributeFormat shader, VertexAttributeFormat app) =>
        (shader, app) switch
        {
            (VertexAttributeFormat.Float4, VertexAttributeFormat.R8G8B8A8Unorm) => true,
            _ => false
        };

    private static Format VertexAttributeFormatToVk(VertexAttributeFormat format) => format switch
    {
        VertexAttributeFormat.Float => Format.R32Sfloat,
        VertexAttributeFormat.Float2 => Format.R32G32Sfloat,
        VertexAttributeFormat.Float3 => Format.R32G32B32Sfloat,
        VertexAttributeFormat.Float4 => Format.R32G32B32A32Sfloat,
        VertexAttributeFormat.Int => Format.R32Sint,
        VertexAttributeFormat.Int2 => Format.R32G32Sint,
        VertexAttributeFormat.Int3 => Format.R32G32B32Sint,
        VertexAttributeFormat.Int4 => Format.R32G32B32A32Sint,
        VertexAttributeFormat.UInt => Format.R32Uint,
        VertexAttributeFormat.UInt2 => Format.R32G32Uint,
        VertexAttributeFormat.UInt3 => Format.R32G32B32Uint,
        VertexAttributeFormat.UInt4 => Format.R32G32B32A32Uint,
        VertexAttributeFormat.R8G8B8A8Unorm => Format.R8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static Silk.NET.Vulkan.PrimitiveTopology PrimitiveTopologyToVk(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => Silk.NET.Vulkan.PrimitiveTopology.PointList,
        PrimitiveTopology.LineList => Silk.NET.Vulkan.PrimitiveTopology.LineList,
        PrimitiveTopology.LineStrip => Silk.NET.Vulkan.PrimitiveTopology.LineStrip,
        PrimitiveTopology.TriangleList => Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
        PrimitiveTopology.TriangleStrip => Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip,
        PrimitiveTopology.TriangleFan => Silk.NET.Vulkan.PrimitiveTopology.TriangleFan,
        _ => throw new ArgumentOutOfRangeException(nameof(topology))
    };

    private static CullModeFlags CullModeToVk(CullMode mode) => mode switch
    {
        CullMode.None => CullModeFlags.None,
        CullMode.Front => CullModeFlags.FrontBit,
        CullMode.Back => CullModeFlags.BackBit,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static Silk.NET.Vulkan.FrontFace FrontFaceToVk(FrontFace frontFace) => frontFace switch
    {
        FrontFace.CounterClockwise => Silk.NET.Vulkan.FrontFace.CounterClockwise,
        FrontFace.Clockwise => Silk.NET.Vulkan.FrontFace.Clockwise,
        _ => throw new ArgumentOutOfRangeException(nameof(frontFace))
    };

    private static Silk.NET.Vulkan.BlendFactor BlendFactorToVk(BlendFactor factor) => factor switch
    {
        BlendFactor.Zero => Silk.NET.Vulkan.BlendFactor.Zero,
        BlendFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        BlendFactor.SrcAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        BlendFactor.DstAlpha => Silk.NET.Vulkan.BlendFactor.DstAlpha,
        BlendFactor.OneMinusDstAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha,
        BlendFactor.SrcColor => Silk.NET.Vulkan.BlendFactor.SrcColor,
        BlendFactor.OneMinusSrcColor => Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor,
        BlendFactor.DstColor => Silk.NET.Vulkan.BlendFactor.DstColor,
        BlendFactor.OneMinusDstColor => Silk.NET.Vulkan.BlendFactor.OneMinusDstColor,
        _ => throw new ArgumentOutOfRangeException(nameof(factor))
    };

    internal static Silk.NET.Vulkan.CompareOp CompareOpToVk(CompareOp op) => op switch
    {
        CompareOp.Never => Silk.NET.Vulkan.CompareOp.Never,
        CompareOp.Less => Silk.NET.Vulkan.CompareOp.Less,
        CompareOp.Equal => Silk.NET.Vulkan.CompareOp.Equal,
        CompareOp.LessOrEqual => Silk.NET.Vulkan.CompareOp.LessOrEqual,
        CompareOp.Greater => Silk.NET.Vulkan.CompareOp.Greater,
        CompareOp.NotEqual => Silk.NET.Vulkan.CompareOp.NotEqual,
        CompareOp.GreaterOrEqual => Silk.NET.Vulkan.CompareOp.GreaterOrEqual,
        CompareOp.Always => Silk.NET.Vulkan.CompareOp.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;
        vk.DeviceWaitIdle(_device.Device);

        vk.DestroyCommandPool(_device.Device, _transferPool, null);

        _gpuOnly.Dispose();
        _cpuToGpu.Dispose();
        _gpuToCpu.Dispose();
        _shaderCompiler.Dispose();
    }
}

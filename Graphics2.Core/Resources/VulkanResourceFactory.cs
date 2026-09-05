using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;
using Silk.NET.Vulkan;
using Silk.NET.Core.Native;

namespace Graphics2.Core.Resources;

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

        return new VkBuffer(_device, allocator, bufferHandle, allocation, memory, description.Size, BufferUsageToMemoryUsage(description.Usage));
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
        var vk = _device.Instance.Vk;
        var format = TextureFormatToVk(description.Format);
        var usage = TextureUsageToVk(description.Usage);

        var createInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(description.Width, description.Height, description.Depth),
            MipLevels = description.MipLevels,
            ArrayLayers = description.ArrayLayers,
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
            ViewType = ImageViewType.Type2D,
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
                LayerCount = description.ArrayLayers
            }
        };

        if (vk.CreateImageView(_device.Device, in viewCreateInfo, null, out var viewHandle) != Result.Success)
        {
            _gpuOnly.Free(allocation);
            vk.DestroyImage(_device.Device, imageHandle, null);
            throw new VkAllocationException("Failed to create image view.");
        }

        return new Texture(_device, _gpuOnly, imageHandle, viewHandle, allocation, description.Width, description.Height, description.Depth, description.Format, description.Usage);
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
        if (compiled.FragmentSpirV is not null && description.FragmentEntryPoint is not null)
        {
            fragmentShader = CreateShader(new ShaderDescription
            {
                SpirV = compiled.FragmentSpirV,
                Stage = ShaderStage.Fragment,
                EntryPoint = "main"
            });
        }

        var descriptorSetLayouts = CreateDescriptorSetLayouts(compiled.Reflection);
        var pipelineLayout = CreatePipelineLayout(descriptorSetLayouts);

        var pipelineHandle = CreateGraphicsPipeline(description, vertexShader, fragmentShader, pipelineLayout, compiled.Reflection);

        return new Pipeline(_device, pipelineHandle, pipelineLayout, descriptorSetLayouts, vertexShader, fragmentShader, compiled.Reflection);
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

        var pixelSize = TextureFormatPixelSize(dst.Format);
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

    private static uint TextureFormatPixelSize(TextureFormat format) => format switch
    {
        TextureFormat.R8G8B8A8Unorm => 4,
        TextureFormat.R8G8B8A8Srgb => 4,
        TextureFormat.B8G8R8A8Srgb => 4,
        TextureFormat.R32G32B32A32Sfloat => 16,
        TextureFormat.D32Sfloat => 4,
        TextureFormat.D24UnormS8Uint => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

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
        for (var i = 0; i < setCount; i++)
        {
            bindingsBySet[i] = [];
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

            bindingsBySet[(int)param.BindingSpace].Add(new DescriptorSetLayoutBinding
            {
                Binding = param.BindingIndex,
                DescriptorType = descriptorType,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                PImmutableSamplers = null
            });
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
            fixed (DescriptorSetLayoutBinding* ptr = bindingsArray)
            {
                var createInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
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

    private PipelineLayout CreatePipelineLayout(DescriptorSetLayout[] descriptorSetLayouts)
    {
        var vk = _device.Instance.Vk;

        fixed (DescriptorSetLayout* ptr = descriptorSetLayouts)
        {
            var createInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = ptr,
                PushConstantRangeCount = 0
            };

            if (vk.CreatePipelineLayout(_device.Device, in createInfo, null, out var layout) != Result.Success)
            {
                throw new VkAllocationException("Failed to create pipeline layout.");
            }

            return layout;
        }
    }

    private Silk.NET.Vulkan.Pipeline CreateGraphicsPipeline(
        PipelineDescription description,
        VkShader vertexShader,
        VkShader? fragmentShader,
        PipelineLayout pipelineLayout,
        ShaderReflection reflection)
    {
        var vk = _device.Instance.Vk;

        var stageCount = fragmentShader is not null ? 2u : 1u;
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
        if (fragmentShader is not null)
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

        var vertexInputs = Array.Empty<VertexAttributeReflection>();
        var vsEntry = Array.Find(reflection.EntryPoints, ep => ep.Stage == ShaderStage.Vertex);
        if (vsEntry.VertexInputs.Length > 0)
        {
            vertexInputs = vsEntry.VertexInputs;
        }

        var bindingDescription = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = 0,
            InputRate = VertexInputRate.Vertex
        };

        var attributeDescriptions = stackalloc VertexInputAttributeDescription[vertexInputs.Length];
        for (var i = 0; i < vertexInputs.Length; i++)
        {
            attributeDescriptions[i] = new VertexInputAttributeDescription
            {
                Binding = 0,
                Location = vertexInputs[i].Location,
                Format = VertexAttributeFormatToVk(vertexInputs[i].Format),
                Offset = 0
            };
        }

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &bindingDescription,
            VertexAttributeDescriptionCount = (uint)vertexInputs.Length,
            PVertexAttributeDescriptions = attributeDescriptions
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

        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            DepthClampEnable = false,
            RasterizerDiscardEnable = false,
            PolygonMode = description.Wireframe ? PolygonMode.Line : PolygonMode.Fill,
            CullMode = CullModeToVk(description.CullMode),
            FrontFace = FrontFaceToVk(description.FrontFace),
            DepthBiasEnable = false,
            LineWidth = 1.0f
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
            var blend = description.Blends is not null && i < description.Blends.Length
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
            DepthAttachmentFormat = description.DepthAttachment is not null
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
        if (usage.HasFlag(BufferUsage.Staging) || usage.HasFlag(BufferUsage.Uniform))
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

        if (usage.HasFlag(BufferUsage.TransferDestination))
        {
            flags |= BufferUsageFlags.TransferDstBit;
        }

        return flags;
    }

    private static Format TextureFormatToVk(TextureFormat format) => format switch
    {
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

    private static Silk.NET.Vulkan.CompareOp CompareOpToVk(CompareOp op) => op switch
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

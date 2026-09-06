using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using IndexType = Graphics2.Core.Resources.IndexType;
using Pipeline = Graphics2.Core.Resources.Pipeline;
// ReSharper disable LoopCanBeConvertedToQuery

// ReSharper disable ForCanBeConvertedToForeach

namespace Graphics2.Core.Render;

public sealed unsafe class RenderPassEncoder : IDisposable
{
    private readonly VkDevice _device;
    private readonly Vk _vk;
    private readonly DescriptorPool _descriptorPool;
    private readonly CommandBuffer _cmd;
    private readonly RenderPassDescriptor _descriptor;
    private readonly Vector2D<int> _defaultExtent;
    private readonly HashSet<ulong> _pendingCopies;
    private Pipeline? _pipeline;
    private readonly List<PendingDescriptorWrite> _pendingWrites = [];
    private bool _descriptorsDirty;
    private bool _ended;

    private RenderPassEncoder(VkDevice device, DescriptorPool descriptorPool, CommandBuffer cmd, RenderPassDescriptor descriptor, Vector2D<int> defaultExtent, HashSet<ulong> pendingCopies)
    {
        _device = device;
        _vk = device.Instance.Vk;
        _descriptorPool = descriptorPool;
        _cmd = cmd;
        _descriptor = descriptor;
        _defaultExtent = defaultExtent;
        _pendingCopies = pendingCopies;
    }

    internal static RenderPassEncoder Begin(VkDevice device, DescriptorPool descriptorPool, CommandBuffer cmd, RenderPassDescriptor descriptor, Vector2D<int> defaultExtent, HashSet<ulong> pendingCopies)
    {
        var encoder = new RenderPassEncoder(device, descriptorPool, cmd, descriptor, defaultExtent, pendingCopies);
        encoder.BeginPass();
        return encoder;
    }

    private void BeginPass()
    {
        var colors = _descriptor.ColorAttachments;
        if (colors == null || colors.Length == 0)
        {
            throw new ArgumentException("RenderPassDescriptor requires at least one color attachment for now", nameof(_descriptor));
        }

        var depth = _descriptor.DepthAttachment;

        var area = _descriptor.RenderArea ?? new Box2D<int>(Vector2D<int>.Zero, _defaultExtent);
        var offset = new Offset2D(area.Min.X, area.Min.Y);
        var extent = new Extent2D((uint)(area.Max.X - area.Min.X), (uint)(area.Max.Y - area.Min.Y));

        var barrierCount = colors.Length + (depth != null ? 1 : 0);
        var preBarriers = stackalloc ImageMemoryBarrier2[barrierCount];
        var preCount = 0;
        for (var i = 0; i < colors.Length; i++)
        {
            var colorAttachment = colors[i];
            var oldLayout = colorAttachment.LoadOp == LoadOp.Load
                ? colorAttachment.Target.CurrentLayout
                : ImageLayout.Undefined;

            if (oldLayout == ImageLayout.ColorAttachmentOptimal)
            {
                continue;
            }

            preBarriers[preCount++] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags2.None,
                DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags2.ColorAttachmentWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = colorAttachment.Target.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
        }

        if (depth != null)
        {
            var oldLayout = depth.LoadOp == LoadOp.Load
                ? depth.Target.CurrentLayout
                : ImageLayout.Undefined;

            if (oldLayout != ImageLayout.DepthAttachmentOptimal)
            {
                preBarriers[preCount++] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    SrcAccessMask = AccessFlags2.None,
                    DstStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    DstAccessMask = AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
                    OldLayout = oldLayout,
                    NewLayout = ImageLayout.DepthAttachmentOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Target.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };
            }
        }

        if (preCount > 0)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)preCount,
                PImageMemoryBarriers = preBarriers
            };

            _vk.CmdPipelineBarrier2(_cmd, &dependencyInfo);
        }

        FlushCopyBarriers();

        var attachments = stackalloc RenderingAttachmentInfo[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var colorAttachment = colors[i];
            attachments[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = colorAttachment.Target.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = colorAttachment.LoadOp switch
                {
                    LoadOp.Clear => AttachmentLoadOp.Clear,
                    LoadOp.Load => AttachmentLoadOp.Load,
                    LoadOp.DontCare => AttachmentLoadOp.DontCare,
                    _ => AttachmentLoadOp.Clear
                },
                StoreOp = colorAttachment.StoreOp switch
                {
                    StoreOp.Store => AttachmentStoreOp.Store,
                    StoreOp.DontCare => AttachmentStoreOp.DontCare,
                    _ => AttachmentStoreOp.Store
                },
                ClearValue = new ClearValue
                {
                    Color = new ClearColorValue(colorAttachment.ClearValue.R, colorAttachment.ClearValue.G, colorAttachment.ClearValue.B, colorAttachment.ClearValue.A)
                }
            };
        }

        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D
            {
                Offset = offset,
                Extent = extent
            },
            LayerCount = 1,
            ColorAttachmentCount = (uint)colors.Length,
            PColorAttachments = attachments
        };

        if (depth != null)
        {
            var depthAttachmentInfo = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depth.Target.View,
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = depth.LoadOp switch
                {
                    LoadOp.Clear => AttachmentLoadOp.Clear,
                    LoadOp.Load => AttachmentLoadOp.Load,
                    LoadOp.DontCare => AttachmentLoadOp.DontCare,
                    _ => AttachmentLoadOp.Clear
                },
                StoreOp = depth.StoreOp switch
                {
                    StoreOp.Store => AttachmentStoreOp.Store,
                    StoreOp.DontCare => AttachmentStoreOp.DontCare,
                    _ => AttachmentStoreOp.Store
                },
                ClearValue = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue(depth.ClearDepth, 0)
                }
            };
            
            renderingInfo.PDepthAttachment = &depthAttachmentInfo;
        }

        _vk.CmdBeginRendering(_cmd, &renderingInfo);
    }

    public void SetPipeline(Pipeline pipeline)
    {
        _pipeline = pipeline;
        _pendingWrites.Clear();
        _descriptorsDirty = false;
        _vk.CmdBindPipeline(_cmd, PipelineBindPoint.Graphics, pipeline.Handle);
    }

    public void BindUniformBuffer(string name, VkBuffer buffer)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.UniformBuffer)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not UniformBuffer.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.UniformBuffer,
            BufferHandle = buffer.Handle,
            Offset = 0,
            Range = (ulong)buffer.Size
        });

        _descriptorsDirty = true;
    }

    public void BindUniformBuffer(string name, VkBuffer buffer, ulong offset, ulong range)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.UniformBuffer)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not UniformBuffer.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.UniformBuffer,
            BufferHandle = buffer.Handle,
            Offset = offset,
            Range = range
        });

        _descriptorsDirty = true;
    }

    public void BindUniformBuffer(string name, RingAllocation allocation)
    {
        BindUniformBuffer(name, allocation.Buffer, allocation.Offset, allocation.Size);
    }

    public void BindStorageBuffer(string name, VkBuffer buffer)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.StorageBuffer)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not StorageBuffer.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.StorageBuffer,
            BufferHandle = buffer.Handle,
            Offset = 0,
            Range = (ulong)buffer.Size
        });

        _descriptorsDirty = true;
    }

    public void BindStorageBuffer(string name, VkBuffer buffer, ulong offset, ulong range)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.StorageBuffer)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not StorageBuffer.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.StorageBuffer,
            BufferHandle = buffer.Handle,
            Offset = offset,
            Range = range
        });

        _descriptorsDirty = true;
    }

    public void BindStorageBuffer(string name, RingAllocation allocation)
    {
        BindStorageBuffer(name, allocation.Buffer, allocation.Offset, allocation.Size);
    }

    public void BindTexture(string name, VkTexture texture)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.SampledImage)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not SampledImage.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.SampledImage,
            ImageView = texture.View,
            ImageLayout = texture.CurrentLayout
        });

        _descriptorsDirty = true;
    }

    /// <summary>
    ///     Binds a <see cref="TextureArray" /> to the pipeline by name.
    ///     The shader parameter must be a fixed-size <c>SampledImage</c> array in its own descriptor set (no other parameters in the same set).
    ///     The registry's capacity must match the shader's declared array size.
    ///     This is a separate path from the per-draw descriptor allocation path.
    ///     The registry's pre-built descriptor set is bound directly.
    ///     Call once per pipeline setup; the set stays bound until another set is bound to the same slot.
    /// </summary>
    public void BindTextureArray(string name, TextureArray array)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.SampledImage)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not SampledImage.", nameof(name));
        }

        if (parameter.ElementCount <= 1)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is not an array. Use BindTexture for single textures.", nameof(name));
        }

        if (parameter.ElementCount != array.Capacity)
        {
            throw new ArgumentException($"Texture registry capacity ({array.Capacity}) does not match shader array size ({parameter.ElementCount}).", nameof(array));
        }

        foreach (var param in _pipeline.Reflection.Parameters)
        {
            if (param.BindingSpace == parameter.BindingSpace && param.Name != name)
            {
                throw new InvalidOperationException($"Set {parameter.BindingSpace} contains other parameters besides \"{name}\". The bindless texture set must be its own set.");
            }
        }

        var set = array.Set;
        _vk.CmdBindDescriptorSets(_cmd, PipelineBindPoint.Graphics, _pipeline.Layout, parameter.BindingSpace, 1, &set, 0, null);
    }

    public void BindSampler(string name, VkSampler sampler)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.Sampler)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not Sampler.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.Sampler,
            Sampler = sampler.Handle
        });

        _descriptorsDirty = true;
    }

    public void BindStorageImage(string name, VkTexture texture)
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before binding resources.");
        }

        if (!_pipeline.TryGetParameter(name, out var parameter))
        {
            throw new ArgumentException($"Shader parameter \"{name}\" not found in pipeline reflection.", nameof(name));
        }

        if (parameter.Kind != ShaderParameterKind.StorageImage)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is {parameter.Kind}, not StorageImage.", nameof(name));
        }

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.StorageImage,
            ImageView = texture.View,
            ImageLayout = texture.CurrentLayout
        });

        _descriptorsDirty = true;
    }

    public void PushConstants<T>(in T data) where T : unmanaged
    {
        if (_pipeline == null)
        {
            throw new InvalidOperationException("SetPipeline must be called before pushing constants.");
        }

        if (_pipeline.PushConstantSize == 0)
        {
            throw new InvalidOperationException("Pipeline has no push constants.");
        }

        var size = (uint)sizeof(T);
        if (size != _pipeline.PushConstantSize)
        {
            throw new ArgumentException($"Push constant size mismatch: data is {size} bytes, pipeline expects {_pipeline.PushConstantSize} bytes.", nameof(data));
        }

        fixed (T* ptr = &data)
        {
            _vk.CmdPushConstants(_cmd, _pipeline.Layout, _pipeline.PushConstantStageFlags, 0, size, ptr);
        }
    }

    public void SetViewport(float x, float y, float width, float height, float minDepth = 0.0f, float maxDepth = 1.0f)    {
        var viewport = new Viewport
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            MinDepth = minDepth,
            MaxDepth = maxDepth
        };
        
        _vk.CmdSetViewport(_cmd, 0, 1, &viewport);
    }

    public void SetScissor(int x, int y, uint width, uint height)
    {
        var scissor = new Rect2D
        {
            Offset = new Offset2D(x, y),
            Extent = new Extent2D(width, height)
        };
        
        _vk.CmdSetScissor(_cmd, 0, 1, &scissor);
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        FlushDescriptors();
        _vk.CmdDraw(_cmd, vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public void SetVertexBuffer(VkBuffer buffer, ulong offset = 0)
    {
        var handle = buffer.Handle;
        _vk.CmdBindVertexBuffers(_cmd, 0, 1, &handle, &offset);
    }

    public void SetIndexBuffer(VkBuffer buffer, IndexType indexType, ulong offset = 0)
    {
        _vk.CmdBindIndexBuffer(_cmd, buffer.Handle, offset, IndexTypeToVk(indexType));
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        FlushDescriptors();
        _vk.CmdDrawIndexed(_cmd, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    public void End()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;

        _vk.CmdEndRendering(_cmd);

        var colors = _descriptor.ColorAttachments;
        var depth = _descriptor.DepthAttachment;
        var barrierCount = colors.Length + (depth != null ? 1 : 0);
        var postBarriers = stackalloc ImageMemoryBarrier2[barrierCount];
        var postCount = 0;
        for (var i = 0; i < colors.Length; i++)
        {
            var colorAttachment = colors[i];
            var finalVkLayout = ToVkLayout(colorAttachment.FinalLayout);
            colorAttachment.Target.CurrentLayout = finalVkLayout;

            if (finalVkLayout is ImageLayout.ColorAttachmentOptimal or ImageLayout.Undefined)
            {
                continue;
            }

            postBarriers[postCount++] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags2.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags2.None,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = finalVkLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = colorAttachment.Target.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
        }

        if (depth != null)
        {
            var finalVkLayout = ToVkLayout(depth.FinalLayout);
            depth.Target.CurrentLayout = finalVkLayout;

            if (finalVkLayout is ImageLayout.DepthAttachmentOptimal or ImageLayout.Undefined)
            {
                // Ignored. Staying in DepthAttachmentOptimal, or discarding.
            }
            else
            {
                postBarriers[postCount++] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    SrcAccessMask = AccessFlags2.DepthStencilAttachmentWriteBit,
                    DstStageMask = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                    DstAccessMask = AccessFlags2.None,
                    OldLayout = ImageLayout.DepthAttachmentOptimal,
                    NewLayout = finalVkLayout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Target.Image,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    }
                };
            }
        }

        if (postCount > 0)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)postCount,
                PImageMemoryBarriers = postBarriers
            };

            _vk.CmdPipelineBarrier2(_cmd, &dependencyInfo);
        }
    }

    public void Dispose() => End();

    private void FlushDescriptors()
    {
        if (!_descriptorsDirty || _pendingWrites.Count == 0 || _pipeline == null)
        {
            return;
        }

        var setLayouts = _pipeline.DescriptorSetLayouts;
        if (setLayouts.Length == 0)
        {
            _descriptorsDirty = false;
            return;
        }

        // Pending writes are grouped by set (binding space).
        // Each set gets its own descriptor set allocated from the pool, written, and bound separately.
        // We need to find how many sets we need to touch. The highest set index referenced by any pending write, plus one.
        var maxSet = 0u;
        for (var pendingWriteIndex = 0; pendingWriteIndex < _pendingWrites.Count; pendingWriteIndex++)
        {
            var write = _pendingWrites[pendingWriteIndex];

            if (write.Set > maxSet)
            {
                maxSet = write.Set;
            }
        }

        var setCount = (int)(maxSet + 1);
        var allocatedSets = new DescriptorSet[setCount];

        // We will stackalloc the arrays once, with the maximum size needed.
        // So, we need to find the set with the most pending writes so the arrays are big enough for any single set.
        var maxWritesPerSet = 0;
        for (var setIndex = 0; setIndex < setCount; setIndex++)
        {
            var count = 0;
            for (var pendingWriteIndex = 0; pendingWriteIndex < _pendingWrites.Count; pendingWriteIndex++)
            {
                var write = _pendingWrites[pendingWriteIndex];

                if (write.Set == setIndex)
                {
                    count++;
                }
            }

            if (count > maxWritesPerSet)
            {
                maxWritesPerSet = count;
            }
        }

        var writes = stackalloc WriteDescriptorSet[maxWritesPerSet];
        var bufferInfos = stackalloc DescriptorBufferInfo[maxWritesPerSet];
        var imageInfos = stackalloc DescriptorImageInfo[maxWritesPerSet];

        // To process each set:
        // - we allocate from the pool
        // - fill the write array with this set's pending writes
        // - update the set
        // - then bind it to the command buffer
        for (var setIndex = 0; setIndex < setCount; setIndex++)
        {
            // Count how many pending writes target this set:
            var writeCount = 0;
            for (var pendingWriteIndex = 0; pendingWriteIndex < _pendingWrites.Count; pendingWriteIndex++)
            {
                var write = _pendingWrites[pendingWriteIndex];
                if (write.Set == setIndex)
                {
                    writeCount++;
                }
            }

            if (writeCount == 0)
            {
                continue;
            }

            if (setIndex >= setLayouts.Length || setLayouts[setIndex].Handle == 0)
            {
                throw new InvalidOperationException($"Pipeline has no descriptor set layout for set {setIndex}.");
            }

            // Allocate one descriptor set from the pool, matching this set's layout.
            var layout = setLayouts[setIndex];
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };

            if (_vk.AllocateDescriptorSets(_device.Device, in allocInfo, out allocatedSets[setIndex]) != Result.Success)
            {
                throw new VkException("Failed to allocate descriptor set.");
            }

            // We need to build the write array.
            // One WriteDescriptorSet per pending write for this set.
            var idx = 0;
            for (var pendingWriteIndex = 0; pendingWriteIndex < _pendingWrites.Count; pendingWriteIndex++)
            {
                var write = _pendingWrites[pendingWriteIndex];
                if (write.Set != setIndex)
                {
                    continue;
                }

                if (write.Type is DescriptorType.UniformBuffer or DescriptorType.StorageBuffer)
                {
                    bufferInfos[idx] = new DescriptorBufferInfo
                    {
                        Buffer = write.BufferHandle,
                        Offset = write.Offset,
                        Range = write.Range
                    };

                    writes[idx] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = allocatedSets[setIndex],
                        DstBinding = write.Binding,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = write.Type,
                        PBufferInfo = &bufferInfos[idx]
                    };
                }
                else
                {
                    imageInfos[idx] = new DescriptorImageInfo
                    {
                        Sampler = write.Sampler,
                        ImageView = write.ImageView,
                        ImageLayout = write.ImageLayout
                    };

                    writes[idx] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = allocatedSets[setIndex],
                        DstBinding = write.Binding,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = write.Type,
                        PImageInfo = &imageInfos[idx]
                    };
                }

                idx++;
            }
            
            _vk.UpdateDescriptorSets(
                _device.Device,
                (uint)writeCount,
                writes, 
                0,
                null
            );
           
            var setHandle = allocatedSets[setIndex];
            
            _vk.CmdBindDescriptorSets(
                _cmd, 
                PipelineBindPoint.Graphics,
                _pipeline.Layout,
                (uint)setIndex,
                1,
                &setHandle, 
                0,
                null
            );
        }

        _pendingWrites.Clear();
        _descriptorsDirty = false;
    }

    private void FlushCopyBarriers()
    {
        if (_pendingCopies.Count == 0)
        {
            return;
        }

        var barriers = new BufferMemoryBarrier2[_pendingCopies.Count];
        var i = 0;
        foreach (var handle in _pendingCopies)
        {
            barriers[i++] = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.AllGraphicsBit,
                DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit | AccessFlags2.UniformReadBit | AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = new Silk.NET.Vulkan.Buffer { Handle = handle },
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        fixed (BufferMemoryBarrier2* ptr = barriers)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)barriers.Length,
                PBufferMemoryBarriers = ptr
            };

            _vk.CmdPipelineBarrier2(_cmd, &dependencyInfo);
        }

        _pendingCopies.Clear();
    }

    private static ImageLayout ToVkLayout(TextureLayout layout) => layout switch
    {
        TextureLayout.ColorAttachment => ImageLayout.ColorAttachmentOptimal,
        TextureLayout.ShaderRead => ImageLayout.ShaderReadOnlyOptimal,
        TextureLayout.Present => ImageLayout.PresentSrcKhr,
        TextureLayout.None => ImageLayout.Undefined,
        TextureLayout.DepthAttachment => ImageLayout.DepthAttachmentOptimal,
        _ => ImageLayout.ColorAttachmentOptimal
    };

    private static Silk.NET.Vulkan.IndexType IndexTypeToVk(IndexType indexType) => indexType switch
    {
        IndexType.UInt16 => Silk.NET.Vulkan.IndexType.Uint16,
        IndexType.UInt32 => Silk.NET.Vulkan.IndexType.Uint32,
        _ => throw new ArgumentOutOfRangeException(nameof(indexType))
    };

    private struct PendingDescriptorWrite
    {
        public uint Set;
        public uint Binding;
        public DescriptorType Type;
        public Silk.NET.Vulkan.Buffer BufferHandle;
        public ulong Offset;
        public ulong Range;
        public ImageView ImageView;
        public Silk.NET.Vulkan.Sampler Sampler;
        public ImageLayout ImageLayout;
    }
}

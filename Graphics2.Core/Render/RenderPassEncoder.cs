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
    private readonly Frame _frame;
    private readonly VkDevice _device;
    private readonly Vk _vk;
    private readonly DescriptorPool _descriptorPool;
    private readonly CommandBuffer _cmd;
    private readonly RenderPassDescription _description;
    private readonly Vector2D<int> _defaultExtent;
    private readonly HashSet<ulong> _pendingCopies;
    private readonly HashSet<VkTexture> _pendingImageCopies;
    private Pipeline? _pipeline;
    private readonly List<PendingDescriptorWrite> _pendingWrites = [];
    private readonly List<(string Name, TextureArray Array)> _boundTextureArrays = [];
    private bool _descriptorsDirty;
    private bool _passOpened;
    private bool _ended;

    private RenderPassEncoder(Frame frame, RenderPassDescription description)
    {
        _frame = frame;
        _device = frame.Device;
        _vk = frame.Device.Instance.Vk;
        _descriptorPool = frame.DescriptorPool;
        _cmd = frame.CommandBuffer;
        _description = description;
        _defaultExtent = frame.Extent;
        _pendingCopies = frame.PendingCopies;
        _pendingImageCopies = frame.PendingImageCopies;
    }

    internal static RenderPassEncoder Begin(Frame frame, RenderPassDescription description)
    {
        var encoder = new RenderPassEncoder(frame, description);
        encoder.ValidateDescription();
        return encoder;
    }

    private void ValidateDescription()
    {
        var colors = _description.ColorAttachments;
        if (colors == null)
        {
            throw new ArgumentException("RenderPassDescriptor.ColorAttachments must not be null. A depth-only pass passes an empty array.", nameof(_description));
        }

        if (colors.Length == 0 && _description.DepthAttachment == null)
        {
            throw new ArgumentException("RenderPassDescriptor requires a color attachment or a depth attachment.", nameof(_description));
        }

        // A pass is requested here even though nothing is recorded yet, because a copy recorded between this call and the first draw would need a barrier that dynamic rendering forbids:
        _frame.PassOpen = true;
    }

    /// <summary>
    ///     Records everything the pass needs before dynamic rendering opens, then opens it.
    ///     Deferring this to the first draw is what lets the encoder see the bindings and put their transitions in front of the pass, which BeginRenderPass cannot do because no binding has happened yet.
    /// </summary>
    private void EnsurePassOpen()
    {
        if (_passOpened)
        {
            return;
        }

        _passOpened = true;

        var colors = _description.ColorAttachments;
        var depth = _description.DepthAttachment;

        var area = _description.RenderArea ?? new Box2D<int>(Vector2D<int>.Zero, _defaultExtent);
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

            var (srcStages, srcAccess) = LayoutScope(oldLayout);

            // The acquire semaphore is waited on at the color attachment stage (RenderContext.EndFrame), so including that stage is what orders this transition, and the pass it guards, after the acquire:
            srcStages |= PipelineStageFlags2.ColorAttachmentOutputBit;

            // A Clear discards the contents and so reports Undefined, but the transition is itself a write and still has to be ordered after the store the previous pass left in the image:
            srcAccess |= AccessFlags2.ColorAttachmentWriteBit;

            var (dstStages, dstAccess) = LayoutScope(ImageLayout.ColorAttachmentOptimal);

            preBarriers[preCount++] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStages,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStages,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = colorAttachment.Target.Image,
                SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.ColorBit)
            };
        }

        if (depth != null)
        {
            var oldLayout = depth.LoadOp == LoadOp.Load
                ? depth.Target.CurrentLayout
                : ImageLayout.Undefined;

            if (oldLayout != ImageLayout.DepthAttachmentOptimal)
            {
                var (srcStages, srcAccess) = LayoutScope(oldLayout);

                // Same as the color path, the transition of a cleared depth attachment is a write, so its source scope has to cover the fragment tests that performed the previous pass's depth store:
                srcStages |= PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;
                srcAccess |= AccessFlags2.DepthStencilAttachmentWriteBit;

                var (dstStages, dstAccess) = LayoutScope(ImageLayout.DepthAttachmentOptimal);

                preBarriers[preCount++] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = srcStages,
                    SrcAccessMask = srcAccess,
                    DstStageMask = dstStages,
                    DstAccessMask = dstAccess,
                    OldLayout = oldLayout,
                    NewLayout = ImageLayout.DepthAttachmentOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Target.Image,
                    SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.DepthBit)
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

        // The copy flush is what returns a texture uploaded this frame to the layout the array's descriptor recorded, so a bound array is checked here rather than at bind time:
        for (var i = 0; i < _boundTextureArrays.Count; i++)
        {
            var (name, array) = _boundTextureArrays[i];
            array.ValidateRecordedLayouts(name);
        }

        // Every image bound to this pass has to be in a layout its binding can use before dynamic rendering opens, because a barrier cannot be recorded inside it:
        TransitionBoundImages();

        // The tracked layout describes the pass while it is open, so a bind after the first draw sees the layout the image is actually in rather than a stale one:
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i].Target.CurrentLayout = ImageLayout.ColorAttachmentOptimal;
        }

        if (depth != null)
        {
            depth.Target.CurrentLayout = ImageLayout.DepthAttachmentOptimal;
        }

        // The descriptor records the layout read here, so it has to be the layout the image will be in while the pass runs:
        FlushDescriptors();

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
                    Color = new ClearColorValue(
                        colorAttachment.ClearValue.X, 
                        colorAttachment.ClearValue.Y,
                        colorAttachment.ClearValue.Z,
                        colorAttachment.ClearValue.W
                    )
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

        RequireTextureType(name, parameter, texture);
        RequireBindableLayout(name, texture, ImageLayout.ShaderReadOnlyOptimal);
        RequireNotAttachment(name, -1, texture);

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.SampledImage,
            ImageView = texture.View,
            Texture = texture
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

        if (parameter.TextureType != null && parameter.TextureType.Value != array.Type)
        {
            throw new ArgumentException($"Shader parameter \"{name}\" is a {parameter.TextureType} array and the registry holds {array.Type} textures.", nameof(array));
        }

        // A registered texture that is also one of this pass's attachments would be sampled and written at once, and the array's descriptors are written once at Add, so the attachment's layout transition cannot be reflected in them:
        var registeredTextures = array.Textures;
        for (var index = 0; index < registeredTextures.Length; index++)
        {
            var registered = registeredTextures[index];

            if (registered != null)
            {
                RequireNotAttachment(name, index, registered);
            }
        }

        // A bind after the first draw cannot be re-checked at the open, because that has already happened:
        if (_passOpened)
        {
            array.ValidateRecordedLayouts(name);
        }
        else
        {
            _boundTextureArrays.Add((name, array));
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

        RequireTextureType(name, parameter, texture);
        RequireBindableLayout(name, texture, ImageLayout.General);
        RequireNotAttachment(name, -1, texture);

        _pendingWrites.Add(new PendingDescriptorWrite
        {
            Set = parameter.BindingSpace,
            Binding = parameter.BindingIndex,
            Type = DescriptorType.StorageImage,
            ImageView = texture.View,
            Texture = texture
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
        EnsurePassOpen();
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
        EnsurePassOpen();
        FlushDescriptors();
        _vk.CmdDrawIndexed(_cmd, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    public void DrawIndexedIndirect(VkBuffer args, ulong offset, uint drawCount, uint stride = DrawIndexedIndirectCommand.SizeInBytes)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (offset % 4 != 0)
        {
            throw new ArgumentException("The first command must start at a 4-byte aligned offset.", nameof(offset));
        }

        if (stride < DrawIndexedIndirectCommand.SizeInBytes || stride % 4 != 0)
        {
            throw new ArgumentException($"Stride must be at least {DrawIndexedIndirectCommand.SizeInBytes} bytes and a multiple of 4.", nameof(stride));
        }

        EnsurePassOpen();
        FlushDescriptors();
        
        _vk.CmdDrawIndexedIndirect(_cmd, args.Handle, offset, drawCount, stride);
    }

    #region Mesh

    /// <summary>
    ///     Binds the mesh's vertex buffer.
    ///     Does nothing when the mesh has never been built, so callers with a possibly empty mesh can bind and draw fine.
    /// </summary>
    public void SetVertexBuffer<TVertex>(Mesh<TVertex> mesh) where TVertex : unmanaged
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.VertexBuffer == null)
        {
            return;
        }

        SetVertexBuffer(mesh.VertexBuffer);
    }

    /// <summary>
    ///     Binds the mesh's index buffer with the mesh's index type. Does nothing when the mesh has never been built.
    /// </summary>
    public void SetIndexBuffer<TVertex>(Mesh<TVertex> mesh) where TVertex : unmanaged
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IndexBuffer == null)
        {
            return;
        }

        SetIndexBuffer(mesh.IndexBuffer, mesh.IndexType);
    }

    /// <summary>
    ///     Draws every index in the mesh. Emits no draw call for an empty mesh.
    /// </summary>
    public void DrawIndexed<TVertex>(Mesh<TVertex> mesh) where TVertex : unmanaged
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (mesh.IndexCount == 0)
        {
            return;
        }

        DrawIndexed((uint)mesh.IndexCount);
    }

    #endregion

    public void End()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;

        // An empty pass is a legitimate clear, so the open has to happen here when no draw did it:
        EnsurePassOpen();

        _vk.CmdEndRendering(_cmd);
        _frame.PassOpen = false;

        var colors = _description.ColorAttachments;
        var depth = _description.DepthAttachment;
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

            var (srcStages, srcAccess) = LayoutScope(ImageLayout.ColorAttachmentOptimal);
            var (dstStages, dstAccess) = LayoutScope(finalVkLayout);

            postBarriers[postCount++] = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStages,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStages,
                DstAccessMask = dstAccess,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = finalVkLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = colorAttachment.Target.Image,
                SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.ColorBit)
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
                var (srcStages, srcAccess) = LayoutScope(ImageLayout.DepthAttachmentOptimal);
                var (dstStages, dstAccess) = LayoutScope(finalVkLayout);

                postBarriers[postCount++] = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcStageMask = srcStages,
                    SrcAccessMask = srcAccess,
                    DstStageMask = dstStages,
                    DstAccessMask = dstAccess,
                    OldLayout = ImageLayout.DepthAttachmentOptimal,
                    NewLayout = finalVkLayout,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = depth.Target.Image,
                    SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.DepthBit)
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
                        ImageLayout = write.Texture?.CurrentLayout ?? ImageLayout.Undefined
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
        if (_pendingCopies.Count == 0 && _pendingImageCopies.Count == 0)
        {
            return;
        }

        Span<BufferMemoryBarrier2> bufferBarriers = _pendingCopies.Count > 0
            ? stackalloc BufferMemoryBarrier2[_pendingCopies.Count]
            : [];
        var i = 0;
        foreach (var handle in _pendingCopies)
        {
            bufferBarriers[i++] = new BufferMemoryBarrier2
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

        Span<ImageMemoryBarrier2> imageBarriers = _pendingImageCopies.Count > 0
            ? stackalloc ImageMemoryBarrier2[_pendingImageCopies.Count]
            : [];
            
        var j = 0;
        foreach (var tex in _pendingImageCopies)
        {
            imageBarriers[j] = new ImageMemoryBarrier2
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
                Image = tex.Image,
                SubresourceRange = VkTexture.AllSubresources(ImageAspectFlags.ColorBit)
            };
            tex.CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
            j++;
        }

        fixed (BufferMemoryBarrier2* bufferPtr = bufferBarriers)
        fixed (ImageMemoryBarrier2* imagePtr = imageBarriers)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = (uint)bufferBarriers.Length,
                PBufferMemoryBarriers = bufferPtr,
                ImageMemoryBarrierCount = (uint)imageBarriers.Length,
                PImageMemoryBarriers = imagePtr
            };

            _vk.CmdPipelineBarrier2(_cmd, &dependencyInfo);
        }

        _pendingCopies.Clear();
        _pendingImageCopies.Clear();
    }

    private static ImageLayout ToVkLayout(TextureLayout layout) => layout.ToVk();

    /// <summary>
    ///     The stages and accesses a layout implies, used for both sides of a transition.
    ///     The source side comes from the layout the image is leaving and the destination side from the layout it is entering, so that a write this frame is ordered against a sampled read in a later pass.
    ///     Present is ordered by the acquire and present semaphores, which is why it carries no access of its own.
    ///     Undefined carries nothing of its own, because leaving it discards the contents, so a caller whose transition still has to be ordered against an earlier write adds that write's access itself.
    /// </summary>
    private static (PipelineStageFlags2 Stages, AccessFlags2 Access) LayoutScope(ImageLayout layout) => layout switch
    {
        ImageLayout.Undefined => (PipelineStageFlags2.None, AccessFlags2.None),
        ImageLayout.PresentSrcKhr => (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.None),
        ImageLayout.ColorAttachmentOptimal => (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
        ImageLayout.DepthAttachmentOptimal => (
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit
        ),
        ImageLayout.ShaderReadOnlyOptimal => (PipelineStageFlags2.AllGraphicsBit, AccessFlags2.ShaderReadBit),
        ImageLayout.TransferDstOptimal => (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit),
        _ => (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit)
    };

    /// <summary>
    ///     A shader declares how many axes its image has, and Vulkan rejects a view whose type disagrees at draw time, so it is refused here where the parameter name is known.
    ///     A shape this does not describe, such as a cube or a buffer, carries no type to check against.
    /// </summary>
    private static void RequireTextureType(string name, in ShaderParameterReflection parameter, VkTexture texture)
    {
        if (parameter.TextureType == null || parameter.TextureType.Value == texture.Type)
        {
            return;
        }

        throw new ArgumentException($"Shader parameter \"{name}\" is a {parameter.TextureType} and the texture is a {texture.Type}.", nameof(texture));
    }

    /// <summary>
    ///     A texture bound before the first draw is transitioned to a layout its binding can use when the pass opens.
    ///     A texture bound after that has no legal place for the barrier it would need, so it has to be in that layout already.
    ///     Undefined is refused either way, because an image that was never written has no contents to read.
    /// </summary>
    private void RequireBindableLayout(string name, VkTexture texture, ImageLayout target)
    {
        if (texture.CurrentLayout == ImageLayout.Undefined)
        {
            throw new InvalidOperationException(
                $"Texture \"{name}\" has never been written, so it cannot be read. Upload it or render into it before binding it."
            );
        }

        if (_passOpened && texture.CurrentLayout != target && texture.CurrentLayout != ImageLayout.General)
        {
            throw new InvalidOperationException(
                $"Texture \"{name}\" is in {texture.CurrentLayout} and is bound after dynamic rendering opened, where a barrier cannot be recorded. Bind it before the first draw of the pass so the encoder can transition it."
            );
        }
    }

    /// <summary>
    ///     A texture that is one of the pass's own attachments cannot also be bound for sampling or storage, because the pass would read and write it at once.
    ///     The comparison is on the image handle, since the same image can be wrapped by more than one view.
    /// </summary>
    private void RequireNotAttachment(string name, int elementIndex, VkTexture texture)
    {
        var colors = _description.ColorAttachments;

        for (var i = 0; i < colors.Length; i++)
        {
            if (colors[i].Target.Image.Handle == texture.Image.Handle)
            {
                throw FeedbackLoop(name, elementIndex, $"color attachment {i}");
            }
        }

        if (_description.DepthAttachment != null && _description.DepthAttachment.Target.Image.Handle == texture.Image.Handle)
        {
            throw FeedbackLoop(name, elementIndex, "the depth attachment");
        }
    }

    private static InvalidOperationException FeedbackLoop(string name, int elementIndex, string attachment)
    {
        var bound = elementIndex >= 0
            ? $"Texture array element {elementIndex} bound to \"{name}\""
            : $"Texture \"{name}\"";

        return new InvalidOperationException(
            $"{bound} is {attachment} of this pass, so binding it would be a read-write feedback loop. Use a separate image."
        );
    }

    /// <summary>
    ///     Moves every image bound to this pass into a layout its binding can use.
    ///     This is what makes a texture that a copy or an earlier pass wrote in this frame bindable, and it is the reason the pass is opened at the first draw rather than at BeginRenderPass.
    /// </summary>
    private void TransitionBoundImages()
    {
        for (var i = 0; i < _pendingWrites.Count; i++)
        {
            var write = _pendingWrites[i];
            var texture = write.Texture;

            if (texture == null)
            {
                continue;
            }

            var isStorageImage = write.Type == DescriptorType.StorageImage;
            var target = isStorageImage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal;

            // General is a legal sample layout and the storage-image layout, so an image already there does not move:
            if (texture.CurrentLayout == target || (!isStorageImage && texture.CurrentLayout == ImageLayout.General))
            {
                continue;
            }

            var (srcStages, srcAccess) = LayoutScope(texture.CurrentLayout);
            var (dstStages, dstAccess) = LayoutScope(target);

            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = srcStages,
                SrcAccessMask = srcAccess,
                DstStageMask = dstStages,
                DstAccessMask = dstAccess,
                OldLayout = texture.CurrentLayout,
                NewLayout = target,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = texture.Image,
                SubresourceRange = VkTexture.AllSubresources(texture.Aspect)
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };

            _vk.CmdPipelineBarrier2(_cmd, &dependencyInfo);
            texture.CurrentLayout = target;
        }
    }

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
        public VkTexture? Texture;
    }
}

using Graphics2.Core.Bootstrap;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
// ReSharper disable ForCanBeConvertedToForeach

namespace Graphics2.Core.Render;

public sealed unsafe class RenderPassEncoder : IDisposable
{
    private readonly Vk _vk;
    private readonly CommandBuffer _cmd;
    private readonly RenderPassDescriptor _descriptor;
    private readonly Vector2D<int> _defaultExtent;
    private bool _ended;

    private RenderPassEncoder(Vk vk, CommandBuffer cmd, RenderPassDescriptor descriptor, Vector2D<int> defaultExtent)
    {
        _vk = vk;
        _cmd = cmd;
        _descriptor = descriptor;
        _defaultExtent = defaultExtent;
    }

    internal static RenderPassEncoder Begin(VkDevice device, CommandBuffer cmd, RenderPassDescriptor descriptor, Vector2D<int> defaultExtent)
    {
        var vk = device.Instance.Vk;
        var encoder = new RenderPassEncoder(vk, cmd, descriptor, defaultExtent);
        encoder.BeginPass();
        return encoder;
    }

    private void BeginPass()
    {
        if (_descriptor.DepthAttachment is not null)
        {
            throw new NotImplementedException("Depth attachments are not implemented");
        }

        var colors = _descriptor.ColorAttachments;
        if (colors is null || colors.Length == 0)
        {
            throw new ArgumentException("RenderPassDescriptor requires at least one color attachment for now", nameof(_descriptor));
        }

        var area = _descriptor.RenderArea ?? new Box2D<int>(Vector2D<int>.Zero, _defaultExtent);
        var offset = new Offset2D(area.Min.X, area.Min.Y);
        var extent = new Extent2D((uint)(area.Max.X - area.Min.X), (uint)(area.Max.Y - area.Min.Y));

        // We need ColorAttachmentOptimal.
        // Clear/DontCare discard contents.
        // Load preserves.
        var preBarriers = stackalloc ImageMemoryBarrier2[colors.Length];
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

        _vk.CmdBeginRendering(_cmd, &renderingInfo);
    }

    public void End()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;

        _vk.CmdEndRendering(_cmd);
        
        // Now, we transition ColorAttachmentOptimal to whatever is required by the caller.
        var colors = _descriptor.ColorAttachments;
        var postBarriers = stackalloc ImageMemoryBarrier2[colors.Length];
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

    private static ImageLayout ToVkLayout(TextureLayout layout) => layout switch
    {
        TextureLayout.ColorAttachment => ImageLayout.ColorAttachmentOptimal,
        TextureLayout.ShaderRead => ImageLayout.ShaderReadOnlyOptimal,
        TextureLayout.Present => ImageLayout.PresentSrcKhr,
        TextureLayout.None => ImageLayout.Undefined,
        _ => ImageLayout.ColorAttachmentOptimal
    };
}

using Graphics2.Applications;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;
using Graphics2.Windowing;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

var description = new ApplicationDescription
{
    Window = new G2WindowDescription
    {
        WindowTitle = "App"
    },
    Instance = new VkInstanceDescription
    {
        UseValidationLayers = true,
        ValidationShowVerboseSeverity = true
    }
};

using var app = new App(description);

app.Run();

class App(ApplicationDescription description) : Application(description)
{
    private int _frames;

    protected override void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");
    }

    protected override unsafe void OnFrame(double deltaTime, Frame frame)
    {
        var vk = Device.Instance.Vk;

        TransitionImageLayout(vk, frame.CommandBuffer, frame.Image,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            AccessFlags2.None, AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.ColorAttachmentOutputBit, PipelineStageFlags2.ColorAttachmentOutputBit);

        var clearColor = new ClearValue
        {
            Color = new ClearColorValue(0f, 0f, 0f, 1f)
        };

        var attachmentInfo = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = frame.ImageView,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = clearColor
        };

        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D
            {
                Offset = new Offset2D(0, 0),
                Extent = frame.Extent
            },
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachmentInfo
        };

        vk.CmdBeginRendering(frame.CommandBuffer, &renderingInfo);
        vk.CmdEndRendering(frame.CommandBuffer);

        TransitionImageLayout(vk, frame.CommandBuffer, frame.Image,
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
            AccessFlags2.ColorAttachmentWriteBit, AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit, PipelineStageFlags2.ColorAttachmentOutputBit
        );

        if (++_frames >= 30)
        {
            Window.Window.Close();
        }
    }

    private static unsafe void TransitionImageLayout(
        Vk vk, CommandBuffer cmd, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags2 srcAccess, AccessFlags2 dstAccess,
        PipelineStageFlags2 srcStage, PipelineStageFlags2 dstStage)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
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
}

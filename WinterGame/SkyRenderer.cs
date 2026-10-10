using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using WinterGame.Core;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
///     Draws the sky and the sun's disc behind everything else in the world pass, from the frame's <see cref="FrameLighting"/>. Render thread only.
/// </summary>
internal sealed class SkyRenderer : IDisposable
{
    private readonly Pipeline _pipeline;

    public SkyRenderer(VulkanResourceFactory factory)
    {
        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.Shader.slang"),
            VertexEntryPoint = "vsSky",
            FragmentEntryPoint = "fsSky",
            ColorAttachments = [WorldLayer.SceneFormat],
            DepthAttachment = TextureFormat.D32Sfloat,

            // The triangle lies on the far plane, so it fills only the pixels the cleared depth still holds:
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = CompareOp.LessOrEqual,
            CullMode = CullMode.None,
            Blends = [BlendState.None]
        });
    }

    /// <summary>
    ///     Draws into <paramref name="pass"/> after the level, which needs a depth attachment. <paramref name="lighting"/> holds this frame's <see cref="FrameLighting"/>.
    /// </summary>
    public void Draw(Frame frame, RenderPassEncoder pass, RingAllocation lighting)
    {
        WgAssert.DebugIsOnRenderThread();

        pass.SetPipeline(_pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
        pass.BindUniformBuffer("lighting", lighting);
        pass.Draw(3);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
    }
}

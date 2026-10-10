using System.Numerics;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
///     Turns the world pass's linear colour into the swapchain's display colour: the exposure, then the Khronos PBR Neutral curve, written through the swapchain's sRGB encode. Render thread only.
/// </summary>
internal sealed class ToneMapper : IDisposable
{
    private struct PushConstants
    {
        /// <summary>
        ///     The exposure factor in x.
        /// </summary>
        // ReSharper disable once NotAccessedField.Local
        public Vector4 Parameters;
    }

    private readonly Pipeline _pipeline;

    public ToneMapper(VulkanResourceFactory factory)
    {
        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.ToneMap.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.B8G8R8A8Srgb],

            // The pass keeps the world's depth for the debug geometry drawn after the image, which this draw ignores:
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = false,
            DepthWrite = false,
            CullMode = CullMode.None,
            Blends = [BlendState.None],
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>()
        });
    }

    /// <summary>
    ///     Fills <paramref name="pass"/>'s colour attachment from <paramref name="scene"/>, which has the frame's size, scaled by <paramref name="exposure"/>.
    /// </summary>
    public void Draw(Frame frame, RenderPassEncoder pass, VkTexture scene, float exposure)
    {
        WgAssert.DebugIsOnRenderThread();

        pass.SetPipeline(_pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
        pass.BindTexture("sceneColor", scene);
        pass.PushConstants(new PushConstants
        {
            Parameters = new Vector4(exposure, 0.0f, 0.0f, 0.0f)
        });
        pass.Draw(3);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
    }
}

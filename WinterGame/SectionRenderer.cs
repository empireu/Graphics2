using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using WinterGame.Core;
using WinterGame.Utilities;
using WinterGame.World.Mesher;

namespace WinterGame;

/// <summary>
///     Draws the level's sections lit by the frame's <see cref="FrameLighting"/> and shadowed by the sun shadow map. The meshes are <see cref="SectionGeometry"/>'s. Render thread only.
/// </summary>
internal sealed class SectionRenderer : IDisposable
{
    private readonly Pipeline _pipeline;

    public SectionRenderer(VulkanResourceFactory factory)
    {
        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.Shader.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [WorldLayer.SceneFormat],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            CullMode = CullMode.Back,
            FrontFace = FrontFace.CounterClockwise,
            Wireframe = false,
            Blends = [BlendState.None],
            VertexLayout = VertexLayout.FromStruct<VoxelVertex>(),
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["draws"] = SsboLayout.FromStruct<DrawRecord>()
            }
        });
    }

    /// <summary>
    ///     Draws <paramref name="geometry"/>, prepared for this frame, into <paramref name="pass"/>, which needs a depth attachment.
    ///     <paramref name="lighting"/> holds this frame's <see cref="FrameLighting"/>, and <paramref name="shadowMap"/> has to have been rendered in this frame already.
    ///     <paramref name="lightVolume"/> is the <see cref="LightVolume"/> that <paramref name="lighting"/> describes.
    /// </summary>
    public void Draw(Frame frame, RenderPassEncoder pass, SectionGeometry geometry, RingAllocation lighting, VkTexture shadowMap, VkSampler shadowSampler, VkTexture lightVolume, VkSampler lightSampler)
    {
        WgAssert.DebugIsOnRenderThread();

        if (geometry.RunCount == 0)
        {
            return;
        }

        pass.SetPipeline(_pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
        pass.BindUniformBuffer("lighting", lighting);
        pass.BindTexture("shadowMap", shadowMap);
        pass.BindSampler("shadowSampler", shadowSampler);
        pass.BindTexture("lightVolume", lightVolume);
        pass.BindSampler("lightSampler", lightSampler);

        geometry.Draw(pass);
    }

    public void Dispose()
    {
        _pipeline.Dispose();
    }
}

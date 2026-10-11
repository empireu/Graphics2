using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    private readonly Pipeline _depthPipeline;

    public SectionRenderer(VulkanResourceFactory factory)
    {
        var source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.Shader.slang");

        _depthPipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = source,
            VertexEntryPoint = "vsDepth",
            FragmentEntryPoint = "fsDepth",
            ColorAttachments = [WorldLayer.NormalFormat],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            CullMode = CullMode.Back,
            FrontFace = FrontFace.CounterClockwise,
            Blends = [BlendState.None],

            // The prepass reads only the position and the normal, so the layout names those two attributes at their places in the full vertex:
            VertexLayout = new VertexLayout
            {
                Stride = (uint)Unsafe.SizeOf<VoxelVertex>(),
                Attributes =
                [
                    new VertexLayoutAttribute(nameof(VoxelVertex.Position), VertexAttributeFormat.Float3, (uint)Marshal.OffsetOf<VoxelVertex>(nameof(VoxelVertex.Position))),
                    new VertexLayoutAttribute(nameof(VoxelVertex.Normal), VertexAttributeFormat.Float3, (uint)Marshal.OffsetOf<VoxelVertex>(nameof(VoxelVertex.Normal)))
                ]
            },
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["draws"] = SsboLayout.FromStruct<DrawRecord>()
            }
        });

        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = source,
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [WorldLayer.SceneFormat],
            DepthAttachment = TextureFormat.D32Sfloat,

            // The prepass has written the depth, and each pixel is shaded once, by the surface whose depth it holds:
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = CompareOp.LessOrEqual,
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
    ///     Draws the depth and the world normal of <paramref name="geometry"/>, prepared for this frame, into <paramref name="pass"/>, whose colour attachment is in <see cref="WorldLayer.NormalFormat"/>, from the camera of <paramref name="lighting"/>'s <see cref="FrameLighting"/>.
    /// </summary>
    public void DrawDepth(Frame frame, RenderPassEncoder pass, SectionGeometry geometry, RingAllocation lighting)
    {
        WgAssert.DebugIsOnRenderThread();

        if (geometry.RunCount == 0)
        {
            return;
        }

        pass.SetPipeline(_depthPipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
        pass.BindUniformBuffer("lighting", lighting);

        geometry.Draw(pass);
    }

    /// <summary>
    ///     Draws <paramref name="geometry"/>, prepared for this frame, into <paramref name="pass"/>, whose depth attachment holds <see cref="DrawDepth"/>'s depth of the same geometry and camera.
    ///     <paramref name="lighting"/> holds this frame's <see cref="FrameLighting"/>, and <paramref name="shadowMap"/> has to have been rendered in this frame already.
    ///     <paramref name="lightVolume"/> is the <see cref="LightVolume"/> that <paramref name="lighting"/> describes, and <paramref name="ambientOcclusion"/> is this frame's <see cref="AmbientOcclusion"/>.
    ///     <paramref name="materials"/> has to be uploaded.
    /// </summary>
    public void Draw(Frame frame, RenderPassEncoder pass, SectionGeometry geometry, RingAllocation lighting, VkTexture shadowMap, VkSampler shadowSampler, VkTexture lightVolume, VkSampler lightSampler, VkTexture ambientOcclusion, TerrainMaterials materials)
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
        pass.BindTexture("ambientOcclusion", ambientOcclusion);
        pass.BindTexture("materialAlbedo", materials.AlbedoHeight);
        pass.BindSampler("materialSampler", materials.Sampler);

        geometry.Draw(pass);
    }

    public void Dispose()
    {
        _depthPipeline.Dispose();
        _pipeline.Dispose();
    }
}

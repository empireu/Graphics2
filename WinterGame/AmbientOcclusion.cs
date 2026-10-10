using System.Numerics;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Silk.NET.Maths;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
///     Screen space ambient occlusion from the depth prepass and its normals, at full resolution: GTAO into a raw image, then a depth-aware 4x4 average that removes its noise pattern.
///     Both images are owned per frame slot and sized with the frame. The lit pass reads the result at its own pixel and darkens only the sky light with it. Render thread only.
/// </summary>
internal sealed class AmbientOcclusion : IDisposable
{
    /// <summary>
    ///     How far from a surface geometry occludes it, in metres. Occlusion past a light cell is the light volume's.
    /// </summary>
    public const float Radius = 0.75f;

    private struct PushConstants
    {
        /// <summary>
        ///     The tangents of half the horizontal and vertical field of view in xy, the near and far planes in metres in zw.
        /// </summary>
        public Vector4 Projection;

        /// <summary>
        ///     <see cref="Radius"/> in x, and the frame's size in pixels in zw.
        /// </summary>
        public Vector4 Parameters;

        /// <summary>
        ///     The camera's right, up and forward in world space, which turn the prepass's world normals into view space.
        /// </summary>
        public Vector4 ViewRight;
        public Vector4 ViewUp;
        public Vector4 ViewForward;
    }

    private readonly Pipeline _occlusion;
    private readonly Pipeline _filter;
    private Texture[]? _raw;
    private Texture[]? _filtered;

    public AmbientOcclusion(VulkanResourceFactory factory)
    {
        var source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.AmbientOcclusion.slang");

        _occlusion = CreatePipeline(factory, source, "fsOcclusion");
        _filter = CreatePipeline(factory, source, "fsFilter");
    }

    /// <summary>
    ///     Replaces the images with ones of <paramref name="size"/>, one pair per frame slot. Waits for the device, like every resize.
    /// </summary>
    public void OnResize(VulkanResourceFactory factory, Vector2D<int> size, int framesInFlight)
    {
        DisposeTargets();

        _raw = new Texture[framesInFlight];
        _filtered = new Texture[framesInFlight];

        for (var slot = 0; slot < framesInFlight; slot++)
        {
            _raw[slot] = CreateTarget(factory, size);
            _filtered[slot] = CreateTarget(factory, size);
        }
    }

    /// <summary>
    ///     Computes this frame's occlusion from <paramref name="depth"/> and <paramref name="normals"/>, the depth prepass of <paramref name="camera"/>'s view, and returns it ready for sampling. No pass may be open.
    /// </summary>
    public VkTexture Render(Frame frame, VkTexture depth, VkTexture normals, Camera3D camera)
    {
        WgAssert.DebugIsOnRenderThread();

        var slot = (int)frame.SlotIndex;
        var tanHalfHeight = MathF.Tan(0.5f * camera.Fov);
        var tanHalfWidth = tanHalfHeight * frame.Extent.X / frame.Extent.Y;

        var constants = new PushConstants
        {
            Projection = new Vector4(tanHalfWidth, tanHalfHeight, camera.Near, camera.Far),
            Parameters = new Vector4(Radius, 0.0f, frame.Extent.X, frame.Extent.Y),
            ViewRight = new Vector4(camera.Right, 0.0f),
            ViewUp = new Vector4(camera.Up, 0.0f),
            ViewForward = new Vector4(camera.Forward, 0.0f)
        };

        using (var pass = BeginPass(frame, _raw![slot]))
        {
            pass.SetPipeline(_occlusion);
            SetFullFrame(frame, pass);
            pass.BindTexture("sceneDepth", depth);
            pass.BindTexture("sceneNormal", normals);
            pass.PushConstants(constants);
            pass.Draw(3);
        }

        using (var pass = BeginPass(frame, _filtered![slot]))
        {
            pass.SetPipeline(_filter);
            SetFullFrame(frame, pass);
            pass.BindTexture("sceneDepth", depth);
            pass.BindTexture("rawOcclusion", _raw[slot]);
            pass.PushConstants(constants);
            pass.Draw(3);
        }

        return _filtered[slot];
    }

    public void Dispose()
    {
        DisposeTargets();
        _filter.Dispose();
        _occlusion.Dispose();
    }

    private static Pipeline CreatePipeline(VulkanResourceFactory factory, string source, string fragmentEntryPoint)
    {
        return factory.CreatePipeline(new PipelineDescription
        {
            Source = source,
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = fragmentEntryPoint,
            ColorAttachments = [TextureFormat.R8Unorm],
            DepthTest = false,
            DepthWrite = false,
            CullMode = CullMode.None,
            Blends = [BlendState.None],
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>()
        });
    }

    private static Texture CreateTarget(VulkanResourceFactory factory, Vector2D<int> size)
    {
        return factory.CreateTexture(new TextureDescription
        {
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Format = TextureFormat.R8Unorm,
            Usage = TextureUsage.ColorAttachment | TextureUsage.Sampled
        });
    }

    private static RenderPassEncoder BeginPass(Frame frame, Texture target)
    {
        return frame.BeginRenderPass(new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = target,
                    LoadOp = LoadOp.DontCare,
                    StoreOp = StoreOp.Store,
                    FinalLayout = TextureLayout.ShaderRead
                }
            ]
        });
    }

    private static void SetFullFrame(Frame frame, RenderPassEncoder pass)
    {
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
    }

    private void DisposeTargets()
    {
        if (_raw != null)
        {
            foreach (var texture in _raw)
            {
                texture.Dispose();
            }

            _raw = null;
        }

        if (_filtered != null)
        {
            foreach (var texture in _filtered)
            {
                texture.Dispose();
            }

            _filtered = null;
        }
    }
}

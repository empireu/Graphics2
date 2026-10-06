using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Silk.NET.Maths;
using WinterGame.Core;
using WinterGame.Utilities;
using WinterGame.World.Mesher;

namespace WinterGame;

/// <summary>
///     The directional light's cascaded shadow map: <see cref="CascadeCount"/> concentric spheres around the camera, each rendered into its own square tile of one map, side by side in X.
///     There is one map per frame slot, because every frame renders it and the previous frame may still be sampling its own. Render thread only.
/// </summary>
internal sealed class SunShadow : IDisposable
{
    public const int CascadeCount = 3;

    /// <summary>
    ///     The width and height of one cascade's tile in texels.
    /// </summary>
    public const int CascadeSize = 2048;

    public const int MapWidth = CascadeCount * CascadeSize;

    /// <summary>
    ///     How far past a cascade's sphere towards the light it still records casters, in metres.
    /// </summary>
    private const float CasterMargin = 100.0f;

    /// <summary>
    ///     The spacing of the grid the light's pivot is rounded to, in metres.
    /// </summary>
    private const float PivotSpacing = 8.0f;

    /// <summary>
    ///     The radius of each cascade's sphere in metres, smallest first. The last is the camera's far plane.
    /// </summary>
    private static ReadOnlySpan<float> CascadeRadii => [10.0f, 30.0f, 100.0f];

    private struct PushConstants
    {
        // ReSharper disable once NotAccessedField.Local
        public Matrix4x4 ViewProj;
    }

    private readonly Pipeline _pipeline;
    private readonly Texture[] _maps;

    /// <summary>
    ///     The comparison sampler a shader reads the map with: a texel is lit when the reference depth is at or in front of it, and the four nearest results are filtered.
    /// </summary>
    public readonly VkSampler Sampler;

    public SunShadow(VulkanResourceFactory factory, int framesInFlight)
    {
        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.ShadowDepth.slang"),
            VertexEntryPoint = "vsMain",
            ColorAttachments = [],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,

            // Terrain is an open surface with nothing behind its sunward side, so culling either face would lose casters:
            CullMode = CullMode.None,
            DepthBiasSlope = 1.5f,

            // The depth pass reads only the position, so the layout names that one attribute at its place in the full vertex:
            VertexLayout = new VertexLayout
            {
                Stride = (uint)Unsafe.SizeOf<VoxelVertex>(),
                Attributes = [new VertexLayoutAttribute(nameof(VoxelVertex.Position), VertexAttributeFormat.Float3, (uint)Marshal.OffsetOf<VoxelVertex>(nameof(VoxelVertex.Position)))]
            },
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>(),
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["draws"] = SsboLayout.FromStruct<DrawRecord>()
            }
        });

        _maps = new Texture[framesInFlight];

        for (var slot = 0; slot < framesInFlight; slot++)
        {
            _maps[slot] = factory.CreateTexture(new TextureDescription
            {
                Width = MapWidth,
                Height = CascadeSize,
                Format = TextureFormat.D32Sfloat,
                Usage = TextureUsage.DepthAttachment | TextureUsage.Sampled
            });
        }

        Sampler = factory.CreateSampler(new SamplerDescription
        {
            MinFilter = FilterMode.Linear,
            MagFilter = FilterMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnabled = false,
            Compare = CompareOp.LessOrEqual
        });
    }

    /// <summary>
    ///     The map this frame renders into and samples.
    /// </summary>
    public VkTexture GetMap(Frame frame)
    {
        return _maps[frame.SlotIndex];
    }

    public static float GetRadius(int cascade)
    {
        return CascadeRadii[cascade];
    }

    /// <summary>
    ///     The width of one texel of <paramref name="cascade"/> in metres.
    /// </summary>
    public static float GetTexelSize(int cascade)
    {
        return 2.0f * CascadeRadii[cascade] / CascadeSize;
    }

    /// <summary>
    ///     <paramref name="cascade"/> for a camera at <paramref name="center"/> and a light towards <paramref name="lightDirection"/>.
    ///     The box is snapped to whole texels in the light's view, so the texels stay put in the world while the camera moves and shadow edges do not crawl.
    ///     The view turns with the light about a pivot within <see cref="PivotSpacing"/> of the camera, so the texels near the camera slide slowly as the sun moves.
    /// </summary>
    public static ShadowCascade GetCascade(Vector3 center, Vector3 lightDirection, int cascade)
    {
        var radius = GetRadius(cascade);
        var texelSize = GetTexelSize(cascade);

        // The pivot is rounded to a grid, so it stays put while the camera moves and only jumps when the camera crosses a grid line:
        var pivot = new Vector3(
            MathF.Round(center.X / PivotSpacing),
            MathF.Round(center.Y / PivotSpacing),
            MathF.Round(center.Z / PivotSpacing)
        ) * PivotSpacing;

        var up = MathF.Abs(lightDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(pivot, pivot - lightDirection, up);
        var viewCenter = Vector3.Transform(center, view);

        var left = MathF.Floor((viewCenter.X - radius) / texelSize) * texelSize;
        var bottom = MathF.Floor((viewCenter.Y - radius) / texelSize) * texelSize;

        // The view looks down its -Z axis, so a point's distance in front of it is -Z:
        var centerDepth = -viewCenter.Z;

        var projection = Matrix4x4.CreateOrthographicOffCenter(
            left,
            left + 2.0f * radius,
            bottom,
            bottom + 2.0f * radius,
            centerDepth - radius - CasterMargin,
            centerDepth + radius
        );

        return new ShadowCascade
        {
            ViewProj = view * projection,
            Parameters = new Vector4(texelSize, 0.0f, 0.0f, 0.0f)
        };
    }

    /// <summary>
    ///     Renders <paramref name="geometry"/>, prepared for this frame, into each of <paramref name="cascades"/>' tiles of this frame's map, which is left ready for sampling.
    ///     A null <paramref name="geometry"/> only clears the map, for a frame whose light casts nothing. The map is cleared either way, so it can always be bound.
    /// </summary>
    public void Render(Frame frame, SectionGeometry? geometry, ReadOnlySpan<ShadowCascade> cascades)
    {
        WgAssert.DebugIsOnRenderThread();

        if (cascades.Length != CascadeCount)
        {
            throw new ArgumentException($"Expected {CascadeCount} cascades, got {cascades.Length}.", nameof(cascades));
        }

        using var pass = frame.BeginRenderPass(new RenderPassDescription
        {
            RenderArea = new Box2D<int>(Vector2D<int>.Zero, new Vector2D<int>(MapWidth, CascadeSize)),
            DepthAttachment = new DepthAttachment
            {
                Target = GetMap(frame),
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearDepth = 1.0f,
                FinalLayout = TextureLayout.ShaderRead
            }
        });

        if (geometry == null || geometry.RunCount == 0)
        {
            return;
        }

        pass.SetPipeline(_pipeline);

        for (var cascade = 0; cascade < CascadeCount; cascade++)
        {
            pass.SetViewport(cascade * CascadeSize, 0, CascadeSize, CascadeSize);
            pass.SetScissor(cascade * CascadeSize, 0, CascadeSize, CascadeSize);

            pass.PushConstants(new PushConstants
            {
                ViewProj = cascades[cascade].ViewProj
            });

            geometry.Draw(pass);
        }
    }

    public void Dispose()
    {
        Sampler.Dispose();

        foreach (var map in _maps)
        {
            map.Dispose();
        }

        _pipeline.Dispose();
    }
}

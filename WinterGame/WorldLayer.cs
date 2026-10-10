using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Renderer;
using Graphics2.Renderer.Lines;
using Graphics2.Renderer.Quads;
using Graphics2.Renderer.Text;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.Core;
using WinterGame.Simulation;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Lighting;

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame;

internal sealed class WorldLayer : IDisposable
{
    private readonly App _app;

    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture[]? _depthTextures;

    private readonly QuadRenderer2D _quadRenderer2D;
    private readonly QuadRenderer3D _overlayQuadRenderer3D;
    private readonly LineRenderer3D _lineRenderer3D;

    private readonly Camera3D _camera;

    private readonly SectionGeometry _geometry;
    private readonly SectionRenderer _sections;
    private readonly SunShadow _sunShadow;
    private readonly LightVolume _lightVolume;

    /// <summary>
    ///     The light the last <see cref="Render"/> used, for the panel.
    /// </summary>
    private Daylight _daylight;

    /// <summary>
    ///     How far the sun's marker is drawn from the camera, inside the far plane, and its width, both in metres.
    /// </summary>
    private const float SunMarkerDistance = 90.0f;

    private const float SunMarkerSize = 5.0f;

    private static readonly string[] DebugViewNames = ["None", "Shadow cascades", "Light angle", "Sky light", "Sun shadow", "Triangle light angle"];

    /// <summary>
    ///     The index into <see cref="DebugViewNames"/> the world shader shows.
    /// </summary>
    private int _debugView;

    private bool _freezeShadowDirection;

    /// <summary>
    ///     The light direction the shadow cascades are rendered for, which follows the sun unless <see cref="_freezeShadowDirection"/> holds it.
    /// </summary>
    private Vector3 _shadowDirection;

    /// <summary>
    ///     The light engine's changes to its window arrive through <paramref name="lightChannel"/>.
    /// </summary>
    public WorldLayer(App app, Camera3D camera, MessageChannel<LightVolumeMessage> lightChannel)
    {
        _app = app;
        _camera = camera;

        #region Assets

        var factory = app.Factory;

        var font = Font.CreateDefault(48.0f, RasterizationMode.Sdf);

        _textSampler = factory.CreateSampler(new SamplerDescription
        {
            AnisotropyEnabled = false
        });

        _glyphAtlas = new GlyphAtlas(font, factory);

        _textRenderer = new TextRenderer(
            factory,
            _glyphAtlas,
            _textSampler,
            new TextRendererDescription
            {
                ColorFormat = TextureFormat.B8G8R8A8Srgb,
                DepthFormat = TextureFormat.D32Sfloat,
                SdfSmoothing = 0.015f,
                DepthTest = false,
                DepthWrite = false
            });

        #endregion

        _quadRenderer2D = new QuadRenderer2D(factory, null, null, new PrimitiveRendererDescription
        {
            ColorFormat = TextureFormat.B8G8R8A8Srgb,
            DepthFormat = TextureFormat.D32Sfloat
        });

        _overlayQuadRenderer3D = new QuadRenderer3D(factory, null, null, new PrimitiveRendererDescription
        {
            ColorFormat = TextureFormat.B8G8R8A8Srgb,
            DepthFormat = TextureFormat.D32Sfloat,
            DepthTest = false,
            DepthWrite = false
        });

        _lineRenderer3D = new LineRenderer3D(factory, new PrimitiveRendererDescription
        {
            ColorFormat = TextureFormat.B8G8R8A8Srgb,
            DepthFormat = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            Blends = [new BlendState(true, BlendFactor.One, BlendFactor.One)]
        });

        var framesInFlight = (int)_app.Context!.FramesInFlight;

        _geometry = new SectionGeometry(factory, framesInFlight);
        _sections = new SectionRenderer(factory);
        _sunShadow = new SunShadow(factory, framesInFlight);
        _lightVolume = new LightVolume(factory, lightChannel);
    }

    public void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");

        var factory = _app.Factory;

        DisposeDepthTextures();

        var depthTextureCount = (int)_app.Context!.FramesInFlight;
        _depthTextures = new Texture[depthTextureCount];

        for (var slot = 0; slot < depthTextureCount; slot++)
        {
            _depthTextures[slot] = factory.CreateTexture(new TextureDescription
            {
                Width = (uint)framebufferSize.X,
                Height = (uint)framebufferSize.Y,
                Format = TextureFormat.D32Sfloat,
                Usage = TextureUsage.DepthAttachment
            });
        }

        _camera.SetAspect(framebufferSize);
    }

    /// <summary>
    ///     Takes the level's finished meshes from <paramref name="meshReceiver"/> and the light engine's changes. The mesh uploads are recorded into the frame, and the light is uploaded by <see cref="Render"/>.
    /// </summary>
    public void Update(Frame frame, SectionMeshReceiver meshReceiver)
    {
        _geometry.TakeMeshes(frame, meshReceiver);
        _lightVolume.Take();

        if (!_textInitialized)
        {
            _glyphAtlas.PrepopulateCommons(frame);
            _textInitialized = true;
        }
    }

    /// <summary>
    ///     Draws the level lit at <paramref name="timeOfDay"/>, the fraction of a day since midnight, and a box the size of the debug brush around <paramref name="brushTarget"/> when there is one.
    ///     The sun shadow pass comes first, and the world pass samples what it rendered.
    /// </summary>
    public void Render(Frame frame, Vector3? brushTarget, double timeOfDay)
    {
        _daylight = Daylight.At(timeOfDay);

        var frameLighting = new FrameLighting
        {
            ViewProj = _camera.ViewProj,
            LightDirection = new Vector4(_daylight.SunDirection, 0.0f),
            LightRadiance = new Vector4(_daylight.SunRadiance, 0.0f),
            SkyZenith = new Vector4(_daylight.SkyZenith, 0.0f),
            SkyHorizon = new Vector4(_daylight.SkyHorizon, 0.0f),
            LightVolumePlacement = _lightVolume.ShaderParameters,
            DebugOptions = new Vector4(_debugView, 0.0f, 0.0f, 0.0f)
        };

        if (!_freezeShadowDirection)
        {
            _shadowDirection = _daylight.SunDirection;
        }

        for (var cascade = 0; cascade < SunShadow.CascadeCount; cascade++)
        {
            frameLighting.ShadowCascades[cascade] = SunShadow.GetCascade(_camera.Position, _shadowDirection, cascade);
        }

        var lighting = frame.AllocateUniform(Unsafe.SizeOf<FrameLighting>());
        MemoryMarshal.Write(lighting.Data, frameLighting);

        _geometry.Prepare(frame);

        // At night the sun lights nothing, so its map is only cleared:
        _sunShadow.Render(frame, _daylight.SunIsUp ? _geometry : null, frameLighting.ShadowCascades);

        // The copy waits for every command before it, so it comes after the shadow pass, and only the world pass waits for the copy:
        _lightVolume.Upload(frame);

        _overlayQuadRenderer3D.SetCamera(_camera.ViewProj);
        _textRenderer.SetCamera(_camera.ViewProj);
        _lineRenderer3D.SetCamera(_camera.ViewProj, _camera.Position);

        // There is no sky yet, so a marker stands in for the sun. The level draws after it, so the terrain covers it:
        if (_daylight.SunIsUp)
        {
            _overlayQuadRenderer3D.DrawBillboard3D(_camera.Position + _daylight.SunDirection * SunMarkerDistance, _daylight.SunDirection, new Vector2(SunMarkerSize), new Vector4(1.0f, 0.9f, 0.6f, 1.0f));
        }

        if (brushTarget != null)
        {
            var extent = new Vector3(DebugBrush.Radius);
            _lineRenderer3D.DrawBoundingBox(brushTarget.Value - extent, brushTarget.Value + extent, new Vector4(1.0f, 0.5f, 0.1f, 1.0f), 0.02f);
        }

        var worldPassDescription = new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new Vector4(_daylight.SkyHorizon, 1.0f),
                    FinalLayout = TextureLayout.ColorAttachment
                }
            ],
            DepthAttachment = new DepthAttachment
            {
                Target = _depthTextures![frame.SlotIndex],
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearDepth = 1.0f,
                FinalLayout = TextureLayout.DepthAttachment
            }
        };

        using (var worldPass = frame.BeginRenderPass(worldPassDescription))
        {
            _lineRenderer3D.Submit(frame, worldPass);
            _overlayQuadRenderer3D.Submit(frame, worldPass);
            _textRenderer.Submit(frame, worldPass);

            // The light engine places its window on the first tick, and until then there is no volume to light the sections with:
            if (_lightVolume.Texture != null)
            {
                _sections.Draw(frame, worldPass, _geometry, lighting, _sunShadow.GetMap(frame), _sunShadow.Sampler, _lightVolume.Texture, _lightVolume.Sampler);
            }
        }
    }

    public void DrawPanel()
    {
        if (ImGui.Begin("World"))
        {
            ImGui.Text("Sections:");
            ImGui.Text($"Drawn: {_geometry.SectionCount}");
            ImGui.Text($"Vertices: {_geometry.VertexCount}");
            ImGui.Text($"Indices: {_geometry.IndexCount}");
            ImGui.Text($"Draws: {_geometry.RunCount} run(s) over {_geometry.BufferPairCount} buffer pair(s), per pass");
            ImGui.Text($"Voxel size: {WorldConstants.VoxelSize} m");

            ImGui.Separator();
            ImGui.Text("Light:");
            ImGui.Text($"Sun elevation: {MathF.Asin(_daylight.SunDirection.Y) * 180.0f / MathF.PI:F0} deg");

            // The sun's bearing against the view's own, so the panel says where to look for it:
            var sunBearing = MathF.Atan2(Vector3.Dot(_daylight.SunDirection, _camera.Right), Vector3.Dot(_daylight.SunDirection, Camera3D.GetForward(_camera.Yaw, 0.0f))) * 180.0f / MathF.PI;
            ImGui.Text($"Sun bearing: {MathF.Abs(sunBearing):F0} deg {(sunBearing >= 0.0f ? "right" : "left")} of the view");

            ImGui.Combo("Debug view", ref _debugView, DebugViewNames, DebugViewNames.Length);
            ImGui.Checkbox("Freeze shadow direction", ref _freezeShadowDirection);

            for (var cascade = 0; cascade < SunShadow.CascadeCount; cascade++)
            {
                ImGui.Text($"Shadow cascade {cascade}: {SunShadow.CascadeSize} px over {2.0f * SunShadow.GetRadius(cascade):F0} m, {SunShadow.GetTexelSize(cascade) * 100.0f:F1} cm per texel");
            }

            ImGui.Separator();
            ImGui.Text("Camera:");

            var forward = _camera.Forward;
            var facing = MathF.Abs(forward.X) >= MathF.Abs(forward.Z)
                ? forward.X >= 0.0f ? "+X" : "-X"
                : forward.Z >= 0.0f ? "+Z" : "-Z";

            ImGui.Text($"Position: {_camera.Position.X:F1}, {_camera.Position.Y:F1}, {_camera.Position.Z:F1} m");
            ImGui.Text($"Forward: {forward.X:F2}, {forward.Y:F2}, {forward.Z:F2}");
            ImGui.Text($"Facing: {facing}");
            ImGui.Text($"Yaw: {_camera.Yaw * 180.0f / MathF.PI:F0} deg, Pitch: {_camera.Pitch * 180.0f / MathF.PI:F0} deg");

            var arenaStats = _geometry.GetArenaStats();

            ImGui.Separator();
            ImGui.Text("Arena:");
            ImGui.Text($"Vertex: {arenaStats.Vertices.Pages} page(s), {arenaStats.Vertices.Bytes / 1024} KiB");
            ImGui.Text($"Vertex slots: {arenaStats.Vertices.UsedSlots} used, {arenaStats.Vertices.FreeSlots} free");
            ImGui.Text($"Index: {arenaStats.Indices.Pages} page(s), {arenaStats.Indices.Bytes / 1024} KiB");
            ImGui.Text($"Index slots: {arenaStats.Indices.UsedSlots} used, {arenaStats.Indices.FreeSlots} free");
            ImGui.Text($"Total: {arenaStats.Bytes / 1024} KiB");

            var gpuOnly = _app.Factory.GetAllocatorStats(AllocatorFamily.GpuOnly);
            var cpuToGpu = _app.Factory.GetAllocatorStats(AllocatorFamily.CpuToGpu);

            ImGui.Separator();
            ImGui.Text("Allocators:");
            ImGui.Text($"GPU only: {gpuOnly.Pages} page(s), {gpuOnly.BytesUsed} bytes, {gpuOnly.FreeRanges} free range(s)");
            ImGui.Text($"CPU to GPU: {cpuToGpu.Pages} page(s), {cpuToGpu.BytesUsed} bytes, {cpuToGpu.FreeRanges} free range(s)");
        }

        ImGui.End();
    }

    private void DisposeDepthTextures()
    {
        if (_depthTextures == null)
        {
            return;
        }

        foreach (var depthTexture in _depthTextures)
        {
            depthTexture.Dispose();
        }

        _depthTextures = null;
    }

    public void Dispose()
    {
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        DisposeDepthTextures();
        _quadRenderer2D.Dispose();
        _overlayQuadRenderer3D.Dispose();
        _lineRenderer3D.Dispose();
        _sunShadow.Dispose();
        _lightVolume.Dispose();
        _sections.Dispose();
        _geometry.Dispose();
    }
}

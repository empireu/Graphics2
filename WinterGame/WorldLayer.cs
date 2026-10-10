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
using WinterGame.World.Storage;

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame;

internal sealed class WorldLayer : IDisposable
{
    /// <summary>
    ///     The world pass's colour format: linear radiance, which the tone mapper brings to the display.
    /// </summary>
    public const TextureFormat SceneFormat = TextureFormat.R16G16B16A16Sfloat;

    /// <summary>
    ///     The haze's density per metre, which takes a fifth of a surface's colour over about 45 m.
    /// </summary>
    private const float HazeDensity = 0.005f;

    /// <summary>
    ///     Where the meshed terrain ends, in metres from the player. A mesh needs all 26 neighbours of its section loaded, so the ticket's outer ring of sections has none, and the player can stand half a section from their own section's centre.
    /// </summary>
    private const float FogEnd = (GameSimulation.TicketHorizontalRadius - 1.5f) * SectionPos.SectionSize * WorldConstants.VoxelSize;

    private const float FogStart = 0.5f * FogEnd;

    private readonly App _app;

    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture[]? _depthTextures;

    /// <summary>
    ///     The world pass's colour per frame slot, in <see cref="SceneFormat"/>.
    /// </summary>
    private Texture[]? _sceneTextures;

    private readonly QuadRenderer2D _quadRenderer2D;
    private readonly LineRenderer3D _lineRenderer3D;

    private readonly Camera3D _camera;

    private readonly SectionGeometry _geometry;
    private readonly SectionRenderer _sections;
    private readonly SunShadow _sunShadow;
    private readonly LightVolume _lightVolume;
    private readonly SkyRenderer _sky;
    private readonly ExposureMeter _exposure;
    private readonly ToneMapper _toneMapper;

    /// <summary>
    ///     The light the last <see cref="Render"/> used, for the panel.
    /// </summary>
    private Daylight _daylight;

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
        _sky = new SkyRenderer(factory);
        _exposure = new ExposureMeter(factory, framesInFlight);
        _toneMapper = new ToneMapper(factory);
    }

    public void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");

        var factory = _app.Factory;

        DisposeTargets();

        var slotCount = (int)_app.Context!.FramesInFlight;
        _depthTextures = new Texture[slotCount];
        _sceneTextures = new Texture[slotCount];

        for (var slot = 0; slot < slotCount; slot++)
        {
            _depthTextures[slot] = factory.CreateTexture(new TextureDescription
            {
                Width = (uint)framebufferSize.X,
                Height = (uint)framebufferSize.Y,
                Format = TextureFormat.D32Sfloat,
                Usage = TextureUsage.DepthAttachment
            });

            _sceneTextures[slot] = factory.CreateTexture(new TextureDescription
            {
                Width = (uint)framebufferSize.X,
                Height = (uint)framebufferSize.Y,
                Format = SceneFormat,
                Usage = TextureUsage.ColorAttachment | TextureUsage.Sampled
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
    ///     The sun shadow pass comes first, and the world pass samples what it rendered. The world pass renders linear colour, which is metered for the exposure and tone mapped into the swapchain, and the debug geometry is drawn over the result against the world's depth.
    /// </summary>
    public void Render(Frame frame, Vector3? brushTarget, double timeOfDay)
    {
        _daylight = Daylight.At(timeOfDay);

        var viewProj = _camera.ViewProj;
        Matrix4x4.Invert(viewProj, out var inverseViewProj);

        var frameLighting = new FrameLighting
        {
            ViewProj = viewProj,
            InverseViewProj = inverseViewProj,
            CameraPosition = new Vector4(_camera.Position, 1.0f),
            LightDirection = new Vector4(_daylight.SunDirection, 0.0f),
            LightRadiance = new Vector4(_daylight.SunRadiance, 0.0f),
            SkyZenith = new Vector4(_daylight.SkyZenith, 0.0f),
            SkyHorizon = new Vector4(_daylight.SkyHorizon, 0.0f),
            LightVolumePlacement = _lightVolume.ShaderParameters,
            FogParameters = new Vector4(HazeDensity, FogStart, FogEnd, 0.0f),
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

        _textRenderer.SetCamera(viewProj);
        _lineRenderer3D.SetCamera(viewProj, _camera.Position);

        if (brushTarget != null)
        {
            var extent = new Vector3(DebugBrush.Radius);
            _lineRenderer3D.DrawBoundingBox(brushTarget.Value - extent, brushTarget.Value + extent, new Vector4(1.0f, 0.5f, 0.1f, 1.0f), 0.02f);
        }

        var scene = _sceneTextures![frame.SlotIndex];
        var depth = _depthTextures![frame.SlotIndex];

        var worldPassDescription = new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = scene,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    FinalLayout = TextureLayout.ShaderRead
                }
            ],
            DepthAttachment = new DepthAttachment
            {
                Target = depth,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearDepth = 1.0f,
                FinalLayout = TextureLayout.DepthAttachment
            }
        };

        using (var worldPass = frame.BeginRenderPass(worldPassDescription))
        {
            // The light engine places its window on the first tick, and until then there is no volume to light the sections with:
            if (_lightVolume.Texture != null)
            {
                _sections.Draw(frame, worldPass, _geometry, lighting, _sunShadow.GetMap(frame), _sunShadow.Sampler, _lightVolume.Texture, _lightVolume.Sampler);
            }

            // The sky fills only what the level left at the far plane, so it comes last:
            _sky.Draw(frame, worldPass, lighting);
        }

        _exposure.Measure(frame, scene);

        var displayPassDescription = new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.DontCare,
                    StoreOp = StoreOp.Store,
                    FinalLayout = TextureLayout.ColorAttachment
                }
            ],
            DepthAttachment = new DepthAttachment
            {
                Target = depth,
                LoadOp = LoadOp.Load,
                StoreOp = StoreOp.Store,
                FinalLayout = TextureLayout.DepthAttachment
            }
        };

        using (var displayPass = frame.BeginRenderPass(displayPassDescription))
        {
            // A debug view shows its values as they are, so it is not exposed:
            _toneMapper.Draw(frame, displayPass, scene, _debugView == 0 ? _exposure.Exposure : 1.0f);
            _lineRenderer3D.Submit(frame, displayPass);
            _textRenderer.Submit(frame, displayPass);
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

            _exposure.DrawPanel();
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

    private void DisposeTargets()
    {
        if (_depthTextures != null)
        {
            foreach (var depthTexture in _depthTextures)
            {
                depthTexture.Dispose();
            }

            _depthTextures = null;
        }

        if (_sceneTextures != null)
        {
            foreach (var sceneTexture in _sceneTextures)
            {
                sceneTexture.Dispose();
            }

            _sceneTextures = null;
        }
    }

    public void Dispose()
    {
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        DisposeTargets();
        _quadRenderer2D.Dispose();
        _lineRenderer3D.Dispose();
        _sunShadow.Dispose();
        _lightVolume.Dispose();
        _sky.Dispose();
        _exposure.Dispose();
        _toneMapper.Dispose();
        _sections.Dispose();
        _geometry.Dispose();
    }
}

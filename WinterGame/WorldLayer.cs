using System.Numerics;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Renderer.Lines;
using Graphics2.Renderer.Quads;
using Graphics2.Renderer.Text;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.World;

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

    private readonly SectionRenderer _sections;

    public WorldLayer(App app, Camera3D camera)
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

        _sections = new SectionRenderer(factory, (int)_app.Context!.FramesInFlight);
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
    ///     Takes the level's finished meshes from <paramref name="meshReceiver"/>. The uploads are recorded into the frame.
    /// </summary>
    public void Update(Frame frame, SectionMeshReceiver meshReceiver)
    {
        _sections.TakeMeshes(frame, meshReceiver);

        if (!_textInitialized)
        {
            _glyphAtlas.PrepopulateCommons(frame);
            _textInitialized = true;
        }
    }

    public void Render(Frame frame)
    {
        _overlayQuadRenderer3D.SetCamera(_camera.ViewProj);
        _textRenderer.SetCamera(_camera.ViewProj);
        _lineRenderer3D.SetCamera(_camera.ViewProj, _camera.Position);

        var worldPassDescription = new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new Vector4(0.1f, 0.1f, 0.1f, 1f),
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
            _sections.Draw(frame, worldPass, _camera.ViewProj);
        }
    }

    public void DrawPanel()
    {
        if (ImGui.Begin("World"))
        {
            ImGui.Text("Sections:");
            ImGui.Text($"Drawn: {_sections.SectionCount}");
            ImGui.Text($"Vertices: {_sections.VertexCount}");
            ImGui.Text($"Indices: {_sections.IndexCount}");
            ImGui.Text($"Draws: {_sections.RunCount} run(s) over {_sections.BufferPairCount} buffer pair(s)");
            ImGui.Text($"Voxel size: {WorldConstants.VoxelSize} m");

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

            var arenaStats = _sections.GetArenaStats();

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
        _sections.Dispose();
    }
}

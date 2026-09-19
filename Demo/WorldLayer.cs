using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Data;
using Graphics2.ImGui;
using Graphics2.Mathematics;
using Graphics2.Renderer.Quads;
using Graphics2.Renderer.Lines;
using Graphics2.Renderer.Text;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.World;

// ReSharper disable ForCanBeConvertedToForeach

namespace Demo;

internal sealed unsafe class WorldLayer : IDisposable
{
    private readonly App _app;
    
    private readonly ImGuiRenderer _imGui;
    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture[]? _depthTextures;

    private readonly QuadRenderer2D _quadRenderer2D;
    private readonly QuadRenderer3D _overlayQuadRenderer3D;
    private readonly LineRenderer3D _lineRenderer3D;
    
    private readonly Camera3D _camera = new();

    private readonly Pipeline _pipeline;

    private int _seed = 3141;
    private float _voxelsPerUnit = 16.0f;
    private float _surfaceHeight = 16.0f;
    private float _reliefVoxels = 12.0f; 
    private int _size = 32;

    private readonly List<uint> _indexScratch = [];

    private Mesh<SurfaceNetsVertex>? _mesh;
    private int _solidVoxels;
    
    public WorldLayer(App app)
    {
        _app = app;
        
        #region Assets and ImGui

        var factory = app.Factory;

        _imGui = new ImGuiRenderer(factory);
        _imGui.Initialize(
            app.G2Wnd.Window.FramebufferSize,
            () => app.G2Wnd.ClipboardText,
            text => app.G2Wnd.ClipboardText = text
        );

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

        _pipeline = app.Factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "Demo.Shaders.Shader.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.B8G8R8A8Srgb],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            CullMode = CullMode.Back,
            FrontFace = FrontFace.CounterClockwise,
            Wireframe = false,
            Blends = [BlendState.None],
            VertexLayout = VertexLayout.FromStruct<SurfaceNetsVertex>(),
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>()
        });
    }

    private struct PushConstants
    {
        // ReSharper disable once NotAccessedField.Local
        public Matrix4x4 ViewProj;
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

    private void ImGuiPass(Frame frame)
    {
        using var imGuiPass = frame.BeginRenderPass(new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Load,
                    StoreOp = StoreOp.Store,
                    FinalLayout = TextureLayout.Present
                }
            ]
        });

        _imGui.Submit(frame, imGuiPass);
    }
    
    public void OnFrame(double deltaTime, Frame frame, InputSnapshot input)
    {
        if (input.WasKeyPressed(G2Key.Escape))
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _camera.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);
        _imGui.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);

        if (!_textInitialized)
        {
            _glyphAtlas.PrepopulateCommons(frame);
            _textInitialized = true;
        }
        
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
            
            if (_mesh != null)
            {
                worldPass.SetPipeline(_pipeline);
                worldPass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
                worldPass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

                worldPass.PushConstants(new PushConstants
                {
                    ViewProj = _camera.ViewProj
                });
            
                worldPass.SetVertexBuffer(_mesh);
                worldPass.SetIndexBuffer(_mesh);
                worldPass.DrawIndexed(_mesh);
            }
        }
        
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.DockSpaceOverViewport(0U, (ImGuiViewport*) null, ImGuiDockNodeFlags.PassthruCentralNode, (ImGuiWindowClass*) null);
        ImGui.PopStyleColor();
        
         if (ImGui.Begin("Generator"))
        {
            ImGui.InputInt("Cells", ref _size, 10);
            ImGui.InputFloat("Unit", ref _voxelsPerUnit, 1.0f);
            ImGui.InputFloat("Height", ref _surfaceHeight, 0.5f);
            ImGui.InputFloat("Relief", ref _reliefVoxels, 0.5f);

            _size = Math.Clamp(_size, 1, 128);
            _voxelsPerUnit = MathF.Max(_voxelsPerUnit, 0.01f);

            if (_mesh != null && _mesh.IndexCount > 0)
            {
                ImGui.Separator();
                ImGui.Text("Generated state:");
                ImGui.Text($"Solid Voxels: {_solidVoxels}");
                ImGui.Text($"Vertices: {_mesh.VertexCount}");
                ImGui.Text($"Indices: {_mesh.IndexCount}");
                ImGui.Text($"Vertex capacity: {_mesh.VertexCapacity}");
                ImGui.Text($"Index capacity: {_mesh.IndexCapacity}");
            }
            
            if (ImGui.Button("Run"))
            {
                Regenerate(frame);
            }

            var gpuOnly = _app.Factory.GetAllocatorStats(AllocatorFamily.GpuOnly);
            var cpuToGpu = _app.Factory.GetAllocatorStats(AllocatorFamily.CpuToGpu);

            ImGui.Separator();
            ImGui.Text("Allocators:");
            ImGui.Text($"GPU only: {gpuOnly.Pages} page(s), {gpuOnly.BytesUsed} bytes, {gpuOnly.FreeRanges} free range(s)");
            ImGui.Text($"CPU to GPU: {cpuToGpu.Pages} page(s), {cpuToGpu.BytesUsed} bytes, {cpuToGpu.FreeRanges} free range(s)");
        }

        ImGui.End();
        
        ImGuiPass(frame);
    }

    private void Regenerate(Frame frame)
    {
        var generator = new FbmTerrainGenerator(
            _seed,
            _voxelsPerUnit,
            _surfaceHeight,
            _reliefVoxels,
            new SurfaceMaterialClassifier(_surfaceHeight)
        );
        
        var sampleSize = _size + 4;

        var content = Memory3D<byte>.Allocate(
            sampleSize,
            sampleSize,
            sampleSize
        );
        
        var material = Memory3D<byte>.Allocate(
            content.SizeX,
            content.SizeY,
            content.SizeZ
        );
        
        generator.Generate(
            new Vector3i(0, 0, 0),
            new Vector3i(content.SizeX - 1, content.SizeY - 1, content.SizeZ - 1),
            content.Span,
            material.Span
        );
        
        var vertices = new List<SurfaceNetsVertex>();
        var indices = new List<int>();

        SurfaceNetsMesher.Mesh(new SurfaceNetsMeshingDescription
        {
            Content = content,
            IsoLevel = 127,
            Vertices = vertices,
            Indices = indices
        });

        _indexScratch.Clear();

        foreach (var index in indices)
        {
            _indexScratch.Add((uint)index);
        }

        _mesh ??= _app.Factory.CreateMesh<SurfaceNetsVertex>();
        
        _mesh.Rebuild(
            CollectionsMarshal.AsSpan(vertices),
            CollectionsMarshal.AsSpan(_indexScratch),
            frame
        );

        _solidVoxels = 0;

        var data = content.Span.Data;
        
        for (var index = 0; index < data.Length; index++)
        {
            var contentByte = data[index];
            if (contentByte > 127)
            {
                _solidVoxels++;
            }
        }
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
        _imGui.Dispose();
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        DisposeDepthTextures();
        _quadRenderer2D.Dispose();
        _overlayQuadRenderer3D.Dispose();
        _lineRenderer3D.Dispose();
        _pipeline.Dispose();
        _mesh?.Dispose();
    }
}
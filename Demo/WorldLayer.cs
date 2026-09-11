using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.ImGui;
using Graphics2.Math;
using Graphics2.Renderer.Quads;
using Graphics2.Renderer.Lines;
using Graphics2.Renderer.Text;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;

namespace Demo;

internal sealed unsafe class WorldLayer : IDisposable
{
    private readonly App _app;
    
    private readonly ImGuiRenderer _imGui;
    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture? _depthTexture;

    private readonly QuadRenderer2D _quadRenderer2D;
    private readonly QuadRenderer3D _overlayQuadRenderer3D;
    private readonly LineRenderer3D _lineRenderer3D;
    
    private readonly Camera3D _camera = new();

    private readonly Pipeline _pipeline;
    
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

        _depthTexture?.Dispose();
        _depthTexture = factory.CreateTexture(new TextureDescription
        {
            Width = (uint)framebufferSize.X,
            Height = (uint)framebufferSize.Y,
            Format = TextureFormat.D32Sfloat,
            Usage = TextureUsage.DepthAttachment
        });

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

        var vertices = new List<SurfaceNetsVertex>();
        var indices = new List<int>();
        const int radius = 5;

        var sdf = SphereDistanceField(radius);
        SurfaceNetsMesher.Mesh(new SurfaceNetsMeshingDescription
        {
            Content = sdf,
            IsoLevel = 127,
            Vertices = vertices,
            Indices = indices,
            CellMin = new Vector3i(2, 2, 2),
            CellCount = new Vector3i(sdf.SizeX - 4, sdf.SizeY - 4, sdf.SizeZ - 4)
        });

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
                Target = _depthTexture!,
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

            var vertexBuffer = frame.AllocateVertex(sizeof(SurfaceNetsVertex) * vertices.Count);
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)).CopyTo(vertexBuffer.Data);

            var indexBuffer = frame.AllocateVertex(sizeof(uint) * indices.Count);
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)).CopyTo(indexBuffer.Data);
            
            worldPass.SetPipeline(_pipeline);
            worldPass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
            worldPass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

            worldPass.PushConstants(new PushConstants
            {
                ViewProj = _camera.ViewProj
            });
            
            worldPass.SetVertexBuffer(vertexBuffer.Buffer, vertexBuffer.Offset);
            worldPass.SetIndexBuffer(indexBuffer.Buffer, IndexType.UInt32, indexBuffer.Offset);
            worldPass.DrawIndexed((uint)indices.Count);
        }
        
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.DockSpaceOverViewport(0U, (ImGuiViewport*) null, ImGuiDockNodeFlags.PassthruCentralNode, (ImGuiWindowClass*) null);
        ImGui.PopStyleColor();
        
        if (ImGui.Begin("Info"))
        {
            ImGui.Text($"Vtx: {vertices.Count}");
        }

        ImGui.End();
        
        ImGuiPass(frame);
    }


    private static Grid3D<byte> SphereDistanceField(int radius)
    {
        var grid = new Grid3D<byte>(radius * 2 + 5);
        var center = new Vector3(grid.SizeX / 2.0f, grid.SizeY / 2.0f, grid.SizeZ / 2.0f);

        for (var z = 0; z < grid.SizeZ; z++)
        {
            for (var y = 0; y < grid.SizeY; y++)
            {
                for (var x = 0; x < grid.SizeX; x++)
                {
                    var p = new Vector3(x, y, z);
                    var sdf = Vector3.Distance(p, center) - radius;

                    grid[x, y, z] = (byte)Math.Clamp(127 - sdf * 127, 0, 255);
                }
            }
        }

        return grid;
    }
    
    public void Dispose()
    {
        _imGui.Dispose();
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        _depthTexture?.Dispose();
        _quadRenderer2D.Dispose();
        _overlayQuadRenderer3D.Dispose();
        _lineRenderer3D.Dispose();
        _pipeline.Dispose();
    }
}
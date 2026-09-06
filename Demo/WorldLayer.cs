using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Assets;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.ImGui;
using Graphics2.Renderer.Text;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;

namespace Demo;

struct Vertex(Vector2D<float> position)
{
    public Vector2D<float> Position = position;
}

struct PushConstants(float rotation, float time, uint textureIndex2, uint textureIndex3)
{
    public float Rotation = rotation;
    public float Time = time;
    public uint TextureIndex2 = textureIndex2;
    public uint TextureIndex3 = textureIndex3;
}

struct Camera(Matrix4x4 viewProj)
{
    public Matrix4x4 ViewProj = viewProj;
}

internal sealed unsafe class WorldLayer : IDisposable
{
    private readonly App _app;
    
    private readonly Texture _texture;
    private readonly Texture _texture2;
    private readonly Texture _texture3;
    private readonly TextureArray _textureArray;
    private readonly uint _textureIndex2;
    private readonly uint _textureIndex3;
    private readonly VkSampler _sampler;
    private readonly ImGuiRenderer _imGui;
    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture? _depthTexture;

    private readonly Pipeline _pipeline;
    private readonly VkBuffer _vertexBuffer;
    private readonly VkBuffer _indexBuffer;
    private readonly VkBuffer _uvBuffer;
    
    private static readonly Vertex[] BaseVertices =
    [
        new(new Vector2D<float>(-0.5f, -0.5f)),
        new(new Vector2D<float>(0.5f, -0.5f)),
        new(new Vector2D<float>(-0.5f, 0.5f)),
        new(new Vector2D<float>(0.5f, 0.5f))
    ];
    private static readonly Vector2D<float>[] Uvs =
    [
        new(0.0f, 1.0f),
        new(1.0f, 1.0f),
        new(0.0f, 0.0f),
        new(1.0f, 0.0f)
    ];
    
    private int _frames;
    private double _time;
    private readonly Camera3D _camera = new();

    private Texture LoadTexture(string name)
    {
        var dir = AppContext.BaseDirectory;
        using var stream = File.OpenRead(Path.Combine(dir, name));
        return _app.Factory.LoadTexture(stream);
    }
    
    public WorldLayer(App app)
    {
        _app = app;

        #region Assets and ImGui

        var factory = app.Factory;
        
        _texture = LoadTexture("image1.png");
        _texture2 = LoadTexture("image2.png");
        _texture3 = LoadTexture("image3.png");
        
        _textureArray = factory.CreateTextureArray(1024);
        _textureIndex2 = _textureArray.Add(_texture2);
        _textureIndex3 = _textureArray.Add(_texture3);

        _sampler = factory.CreateSampler(new SamplerDescription
        {
            AnisotropyEnabled = true
        });

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
                SdfSmoothing = 0.015f
            });

        #endregion

        #region Resources

        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = Assets.GetResourceText("Demo.Shaders.Shader.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.B8G8R8A8Srgb],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = false,
            DepthWrite = false,
            CullMode = CullMode.None,
            VertexLayout = VertexLayout.FromStruct<Vertex>(),
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>()
        });

        _vertexBuffer = factory.CreateBuffer(new BufferDescription
        {
            Size = sizeof(Vertex) * 4,
            Usage = BufferUsage.Vertex | BufferUsage.TransferDestination
        });

        _indexBuffer = factory.CreateBuffer(new BufferDescription
        {
            Size = sizeof(ushort) * 6,
            Usage = BufferUsage.Index | BufferUsage.TransferDestination
        });

        var indices = new ReadOnlySpan<ushort>([0, 1, 2, 2, 1, 3]);
        factory.UpdateBuffer(_indexBuffer, MemoryMarshal.AsBytes(indices));

        _uvBuffer = factory.CreateBuffer(new BufferDescription
        {
            Size = sizeof(Vector2D<float>) * 4,
            Usage = BufferUsage.Storage | BufferUsage.TransferDestination
        });

        factory.UpdateBuffer(_uvBuffer, MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2D<float>>(Uvs)));

        #endregion
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
    
    private string _text = string.Empty;
    private readonly List<double> _averageTime = new();

    public void OnFrame(double deltaTime, Frame frame, InputSnapshot input)
    {
        if (input.WasKeyPressed(G2Key.Escape))
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _camera.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);

        _imGui.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.DockSpaceOverViewport(0U, (ImGuiViewport*) null, ImGuiDockNodeFlags.PassthruCentralNode, (ImGuiWindowClass*) null);
        ImGui.PopStyleColor();

        if (_averageTime.Count > 30)
        {
            _averageTime.RemoveAt(0);
        }

        _averageTime.Add(deltaTime);

        if (ImGui.Begin("Info"))
        {
            ImGui.Text($"Frames: {_frames++}");
            ImGui.Text($"FPS: {1.0 / (_averageTime.Sum() / _averageTime.Count):F2}");
            ImGui.InputText("Text", ref _text, 128);
        }

        ImGui.End();

        if (ImGui.Begin("Camera"))
        {
            var pos = _camera.Position;
            ImGui.Text($"Pos: {pos:F2}");
            ImGui.Text($"Yaw: {_camera.Yaw:F3}  Pitch: {_camera.Pitch:F3}");
            ImGui.Text($"Mode: {(_camera.FpsMode ? "FP" : "Free")}");
        }

        ImGui.End();

        Span<Vertex> vertices = stackalloc Vertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = new Vertex(new Vector2D<float>(1.0f, 1.0f) + BaseVertices[i].Position * 0.1f);
        }

        var vertexAlloc = frame.Allocate((uint)(sizeof(Vertex) * 4), 4);
        MemoryMarshal.AsBytes(vertices).CopyTo(vertexAlloc.Data);
        frame.CopyBuffer(vertexAlloc, _vertexBuffer);

        if (!_textInitialized)
        {
            _glyphAtlas.PrepopulateCommons(frame);
            _textInitialized = true;
        }

        var extent = frame.Extent;
        var ortho = Matrix4x4.CreateOrthographicOffCenter(0, extent.X, 0, extent.Y, -1, 1);
        _textRenderer.SetCamera(ortho);
        _textRenderer.DrawString2D(frame, "Hello, World!", new Vector2(0, 0), new Vector4(1, 1, 1, 1));
        _textRenderer.DrawString2D(frame, $"FPS: {1.0 / (_averageTime.Sum() / _averageTime.Count):F2}", new Vector2(50, 110), new Vector4(0, 1, 0, 1));
        
        using var pass = frame.BeginRenderPass(new RenderPassDescriptor
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new RgbaFloat(0f, 0f, 0f, 1f),
                    FinalLayout = TextureLayout.Present
                }
            ],
            DepthAttachment = new DepthAttachment
            {
                Target = _depthTexture!,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.DontCare,
                ClearDepth = 1.0f,
                FinalLayout = TextureLayout.DepthAttachment
            }
        });

        pass.SetPipeline(_pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

        var cameraAlloc = frame.AllocateUniform((uint)sizeof(Camera));
        var camera = new Camera(_camera.ViewProj);
        MemoryMarshal.Write(cameraAlloc.Data, in camera);
        pass.BindUniformBuffer("Camera", cameraAlloc);

        pass.BindStorageBuffer("uvs", _uvBuffer);
        pass.BindTexture("tex", _texture);
        pass.BindSampler("samp", _sampler);
        pass.BindTextureArray("textures", _textureArray);

        pass.PushConstants(new PushConstants(0.0f, (float)_time, _textureIndex2, _textureIndex3));
        pass.SetVertexBuffer(_vertexBuffer);
        pass.SetIndexBuffer(_indexBuffer, IndexType.UInt16);
        pass.DrawIndexed(6);

        _textRenderer.Flush(frame, pass);

        _textRenderer.SetCamera(_camera.ViewProj);
        _textRenderer.DrawString3DOriented(
            frame,
            "DrawString3D A",
            new Vector3(1, 1, 0),
            -new Vector3(1, 0, 0),
            new Vector3(0, 1, 0),
            1.0f, new Vector4(1, 0, 0, 1)
        );
        
        _textRenderer.DrawString3DBillboard(
            frame,
            "DrawString3D A\nDrawString3D B",
            new Vector3(1, 1, 0),
            _camera.Position,
            _camera.Forward,
            _camera.Fov,
            _app.G2Wnd.Window.FramebufferSize.Y,
            100.0f,
            new Vector4(0, 1, 0, 1),
            anchor: TextAnchor.Center,
            maxWidth: 100
        );
        
        _textRenderer.Flush(frame, pass);
        
        _imGui.Render(frame, pass);

        _time += deltaTime;
    }

    public void Dispose()
    {
        _imGui.Dispose();
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        _pipeline.Dispose();
        _depthTexture?.Dispose();
        _textureArray.Dispose();
        _texture.Dispose();
        _texture2.Dispose();
        _texture3.Dispose();
        _sampler.Dispose();
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        _uvBuffer.Dispose();
    }
}
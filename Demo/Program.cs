using System.Numerics;
using Graphics2.Applications;
using Graphics2.Assets;
using Graphics2.Core;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Windowing;
using Silk.NET.Maths;
using System.Runtime.InteropServices;
using Graphics2.Windowing.Input;
using Graphics2.ImGui;
using ImGuiNET;

var description = new ApplicationDescription
{
    Window = new G2WindowDescription
    {
        WindowTitle = "App"
    },
    Instance = new VkInstanceDescription
    {
        UseValidationLayers = true,
        ValidationShowVerboseSeverity = true
    }
};

using var app = new App(description);

app.Run();

struct Vertex(Vector2D<float> position)
{
    public Vector2D<float> Position = position;
}

struct PushConstants(float rotation, uint textureIndex2, uint textureIndex3)
{
    public float Rotation = rotation;
    public uint TextureIndex2 = textureIndex2;
    public uint TextureIndex3 = textureIndex3;
}

struct Camera(float time)
{
    public float Time = time;
}

class App(ApplicationDescription description) : Application(description)
{
    private const string ShaderSource = """
        struct VertexInput {
            float2 position : POSITION;
            uint vertexId : SV_VertexID;
        };

        struct VertexOutput {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        cbuffer Camera : register(b0) {
            float time;
        };

        struct PushConstants {
            float rotation;
            uint textureIndex2;
            uint textureIndex3;
        };

        [[vk::push_constant]] PushConstants pc;

        StructuredBuffer<float2> uvs;
        Texture2D<float4> tex;
        SamplerState samp;

        [[vk::binding(0, 1)]] Texture2D<float4> textures[1024];

        [shader("vertex")]
        VertexOutput vsMain(VertexInput input) {
            VertexOutput output;
            float c = cos(pc.rotation);
            float s = sin(pc.rotation);
            float2 rotated = float2(
                input.position.x * c - input.position.y * s,
                input.position.x * s + input.position.y * c
            );
            output.position = float4(rotated, 0.0, 1.0);
            output.uv = uvs[input.vertexId];
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            float4 c1 = tex.Sample(samp, input.uv);
            float4 c2 = textures[pc.textureIndex2].Sample(samp, input.uv);
            float4 c3 = textures[pc.textureIndex3].Sample(samp, input.uv);
            float4 blended = (c1 + c2 + c3) / 3.0;
            float pulse = 0.5 + 0.5 * sin(time * 3.0);
            return float4(blended.rgb * pulse, 1.0);
        }
        """;

    private Texture? _depthTexture;
    private Texture? _texture;
    private Texture? _texture2;
    private Texture? _texture3;
    private TextureArray? _textureArray;
    private uint _textureIndex2;
    private uint _textureIndex3;
    private VkSampler? _sampler;
    private Pipeline? _pipeline;
    private VkBuffer? _vertexBuffer;
    private VkBuffer? _indexBuffer;
    private VkBuffer? _uvBuffer;
    private readonly Vertex[] _baseVertices =
    [
        new(new Vector2D<float>(-0.5f, -0.5f)),
        new(new Vector2D<float>(0.5f, -0.5f)),
        new(new Vector2D<float>(-0.5f, 0.5f)),
        new(new Vector2D<float>(0.5f, 0.5f))
    ];
    private readonly Vector2D<float>[] _uvs =
    [
        new(0.0f, 1.0f),
        new(1.0f, 1.0f),
        new(0.0f, 0.0f),
        new(1.0f, 0.0f)
    ];
    private int _frames;
    private double _time;
    private ImGuiRenderer? _imGui;

    protected override unsafe void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");

        _depthTexture?.Dispose();
        _depthTexture = Factory.CreateTexture(new TextureDescription
        {
            Width = (uint)framebufferSize.X,
            Height = (uint)framebufferSize.Y,
            Format = TextureFormat.D32Sfloat,
            Usage = TextureUsage.DepthAttachment
        });

        _pipeline?.Dispose();
        _pipeline = Factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
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

        _vertexBuffer?.Dispose();
        _vertexBuffer = Factory.CreateBuffer(new BufferDescription
        {
            Size = sizeof(Vertex) * 4,
            Usage = BufferUsage.Vertex | BufferUsage.TransferDestination
        });

        _indexBuffer?.Dispose();
        _indexBuffer = Factory.CreateBuffer(new BufferDescription
        {
            Size = sizeof(ushort) * 6,
            Usage = BufferUsage.Index | BufferUsage.TransferDestination
        });

        var indices = new ReadOnlySpan<ushort>([0, 1, 2, 2, 1, 3]);
        Factory.UpdateBuffer(_indexBuffer, MemoryMarshal.AsBytes(indices));

        _uvBuffer?.Dispose();
        _uvBuffer = Factory.CreateBuffer(new BufferDescription
        {
            Size = sizeof(Vector2D<float>) * 4,
            Usage = BufferUsage.Storage | BufferUsage.TransferDestination
        });

        Factory.UpdateBuffer(_uvBuffer, MemoryMarshal.AsBytes(new ReadOnlySpan<Vector2D<float>>(_uvs)));
    }

    protected override void OnStart()
    {
        var dir = AppContext.BaseDirectory;
        using var stream1 = File.OpenRead(Path.Combine(dir, "image1.png"));
        _texture = Factory.LoadTexture(stream1);

        _textureArray = Factory.CreateTextureArray(1024);

        using var stream2 = File.OpenRead(Path.Combine(dir, "image2.png"));
        _texture2 = Factory.LoadTexture(stream2);
        _textureIndex2 = _textureArray.Add(_texture2);

        using var stream3 = File.OpenRead(Path.Combine(dir, "image3.png"));
        _texture3 = Factory.LoadTexture(stream3);
        _textureIndex3 = _textureArray.Add(_texture3);

        _sampler = Factory.CreateSampler(new SamplerDescription
        {
            AnisotropyEnabled = false
        });

        _imGui = new ImGuiRenderer(Factory);
        _imGui.Initialize(
            Window.Window.FramebufferSize,
            () => Window.ClipboardText,
            text => Window.ClipboardText = text
        );
    }

    private string _text = string.Empty;
    private readonly List<double> _averageTime = new();
    
    protected override unsafe void OnFrame(double deltaTime, Frame frame, InputSnapshot input)
    {
        _imGui!.Update((float)deltaTime, input, Window.Window.FramebufferSize);

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
            ImGui.Text($"Frames: {_frames}");
            ImGui.Text($"FPS: {1.0 / (_averageTime.Sum() / _averageTime.Count):F2}");
            ImGui.InputText("Text", ref _text, 128);
        }
        
        ImGui.End();

        var scale = 0.5f + 0.5f * MathF.Sin((float)_time * 3.0f);
        Span<Vertex> vertices = stackalloc Vertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = new Vertex(_baseVertices[i].Position * scale);
        }

        var vertexAlloc = frame.Allocate((uint)(sizeof(Vertex) * 4), 4);
        MemoryMarshal.AsBytes(vertices).CopyTo(vertexAlloc.Data);
        frame.CopyBuffer(vertexAlloc, _vertexBuffer!);

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

        pass.SetPipeline(_pipeline!);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

        var cameraAlloc = frame.AllocateUniform((uint)sizeof(Camera));
        var camera = new Camera((float)_time);
        MemoryMarshal.Write(cameraAlloc.Data, in camera);
        pass.BindUniformBuffer("Camera", cameraAlloc);

        pass.BindStorageBuffer("uvs", _uvBuffer!);
        pass.BindTexture("tex", _texture!);
        pass.BindSampler("samp", _sampler!);
        pass.BindTextureArray("textures", _textureArray!);

        pass.PushConstants(new PushConstants((float)(_time * 2.0), _textureIndex2, _textureIndex3));
        pass.SetVertexBuffer(_vertexBuffer!);
        pass.SetIndexBuffer(_indexBuffer!, IndexType.UInt16);
        pass.DrawIndexed(6);

        _imGui.Render(frame, pass);
        
        _time += deltaTime;
        
        if (++_frames >= 1000)
        {
            Window.Window.Close();
        }
    }

    protected override void OnDispose()
    {
        _imGui?.Dispose();
        _pipeline?.Dispose();
        _depthTexture?.Dispose();
        _textureArray?.Dispose();
        _texture?.Dispose();
        _texture2?.Dispose();
        _texture3?.Dispose();
        _sampler?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _uvBuffer?.Dispose();
    }
}

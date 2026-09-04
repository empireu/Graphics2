using Graphics2.Applications;
using Graphics2.Core;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Windowing;
using Silk.NET.Maths;

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

class App(ApplicationDescription description) : Application(description)
{
    private const string ShaderSource = """
        struct VertexOutput {
            float4 position : SV_POSITION;
            float3 color : COLOR;
        };

        [shader("vertex")]
        VertexOutput vsMain(uint vertexId : SV_VertexID) {
            VertexOutput output;
            float2 positions[3] = {
                float2(-0.5, -0.5),
                float2(0.5, -0.5),
                float2(0.0, 0.5)
            };
            float3 colors[3] = {
                float3(1.0, 0.0, 0.0),
                float3(0.0, 1.0, 0.0),
                float3(0.0, 0.0, 1.0)
            };
            output.position = float4(positions[vertexId], 0.0, 1.0);
            output.color = colors[vertexId];
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            return float4(input.color, 1.0);
        }
        """;

    private Texture? _depthTexture;
    private Pipeline? _pipeline;
    private int _frames;

    protected override void OnResize(Vector2D<int> framebufferSize)
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
            CullMode = CullMode.None
        });
    }

    protected override void OnFrame(double deltaTime, Frame frame)
    {
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
        pass.Draw(3);

        if (++_frames >= 30)
        {
            Window.Window.Close();
        }
    }

    protected override void OnDispose()
    {
        _pipeline?.Dispose();
        _depthTexture?.Dispose();
    }
}

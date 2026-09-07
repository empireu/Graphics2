using System.Numerics;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.ImGui;
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
    
    private int _frames;
    private double _time;
    private readonly Camera3D _camera = new();
    
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

        if (!_textInitialized)
        {
            _glyphAtlas.PrepopulateCommons(frame);
            _textInitialized = true;
        }
        
        _textRenderer.DrawString2D(frame, "Hello, World!", new Vector2(0, 0), new Vector4(1, 1, 1, 1));
        _textRenderer.DrawString2D(frame, $"FPS: {1.0 / (_averageTime.Sum() / _averageTime.Count):F2}", new Vector2(50, 110), new Vector4(0, 1, 0, 1));

        var screenPassDescription = new RenderPassDescription
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
            ]
        };

        using (var screenPass = frame.BeginRenderPass(screenPassDescription))
        {
            var extent = frame.Extent;
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, extent.X, 0, extent.Y, -1, 1);
            _textRenderer.SetCamera(ortho);
            _textRenderer.Submit(frame, screenPass);
        }
        
        _overlayQuadRenderer3D.SetCamera(_camera.ViewProj);
        _textRenderer.SetCamera(_camera.ViewProj);
        _lineRenderer3D.SetCamera(_camera.ViewProj, _camera.Position);

        const float gizmoRadius = 2;
        const float axisThickness = 0.05f;
        const float gimbalThickness = 0.05f;
        const int gizmoSegments = 128;

        #region Labels

        void SubmitLabel(string text, Vector3 unit)
        {
            var size = _textRenderer.DrawString3DBillboard(
                frame, text,
                gizmoRadius * unit,
                _camera.Forward,
                0.25f, 
                new Vector4(
                    Math.Abs(unit.X),
                    Math.Abs(unit.Y),
                    Math.Abs(unit.Z),
                1),
                anchor: TextAnchor.Center
            );
        
            _overlayQuadRenderer3D.DrawBillboard3D(
                gizmoRadius * unit,
                _camera.Forward,
                new Vector2(size.X, size.Y),
                new Vector4(1, 1, 1, 1.0f) - new Vector4(Math.Abs(unit.X), Math.Abs(unit.Y), Math.Abs(unit.Z), 0.0f)
            );
        }
        
        SubmitLabel("+X", new Vector3(+1, 0, 0));
        SubmitLabel("-X", new Vector3(-1, 0, 0));

        SubmitLabel("+Y", new Vector3(0, +1, 0));
        SubmitLabel("-Y", new Vector3(0, -1, 0));

        SubmitLabel("+Z", new Vector3(0, 0, +1));
        SubmitLabel("-Z", new Vector3(0, 0, -1));
        
        #endregion
        
        _lineRenderer3D.DrawLine3D(
            new Vector3(-gizmoRadius, 0, 0),
            new Vector3(+gizmoRadius, 0, 0),
            axisThickness,
            new Vector4(1, 0, 0, 1),
            new Vector4(0.5f, 0, 0, 1)
        );
        
        _lineRenderer3D.DrawLine3D(
            new Vector3(0, -gizmoRadius, 0),
            new Vector3(0, +gizmoRadius, 0),
            axisThickness,
            new Vector4(0, 1f, 0, 1),
            new Vector4(0, 0.5f, 0, 1)
        );
        
        _lineRenderer3D.DrawLine3D(
            new Vector3(0, 0, -gizmoRadius),
            new Vector3(0, 0, +gizmoRadius),
            axisThickness,
            new Vector4(0, 1, 0, 1),
            new Vector4(0, 0.5f, 0, 1)
        );

        Span<Vector3> directions = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

        for (var index = 0; index < directions.Length; index++)
        {
            var normal = directions[index];
            var perpendicular = directions[(index + 1) % 3];
            
            for (var i = 1; i <= gizmoSegments; i++)
            {
                var q1 = Quaternion.CreateFromAxisAngle(normal, (i - 1) / (float)gizmoSegments * MathF.PI * 2.0f);
                var q2 = Quaternion.CreateFromAxisAngle(normal, i / (float)gizmoSegments * MathF.PI * 2.0f);

                var r1 = gizmoRadius * Vector3.Transform(perpendicular, q1);
                var r2 = gizmoRadius * Vector3.Transform(perpendicular, q2);
                
                _lineRenderer3D.DrawLine3D(r1, r2, gimbalThickness, new Vector4(normal.X, normal.Y, normal.Z, 0.25f));
            }
        }
        
        using var pass = frame.BeginRenderPass(new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Load,
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
        
        _lineRenderer3D.Submit(frame, pass);
        _overlayQuadRenderer3D.Submit(frame, pass);
        _textRenderer.Submit(frame, pass);
        _imGui.Submit(frame, pass);

        _time += deltaTime;
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
    }
}
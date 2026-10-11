using System.Numerics;
using Graphics2.Applications;
using Graphics2.Core.Render;
using Graphics2.ImGui;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.Core;
using WinterGame.Screens;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
///     Owns the screens and the session and runs them once per frame.
///     Starting and ending a session are queued and applied at the start of the next frame together with the screen changes, so nothing is torn down while the frame is using it.
/// </summary>
internal sealed class App(ApplicationDescription description) : Application(description)
{
    private enum SessionRequest
    {
        None,
        Start,
        End
    }

    private static readonly Vector4 ClearColor = new(0.1f, 0.1f, 0.1f, 1.0f);

    /// <summary>
    ///     The weight of each frame's GPU timing in its running average, which settles in about a third of a second at 60 Hz.
    /// </summary>
    private const double GpuTimingWeight = 0.05;

    private readonly FrameTimeTracker _tracker = new(4000 * 5);

    /// <summary>
    ///     The running average of each GPU timing by its place in the frame, restarted when the label at that place changes.
    /// </summary>
    private readonly string?[] _gpuLabels = new string?[Frame.MaxTimestamps];
    private readonly double[] _gpuMilliseconds = new double[Frame.MaxTimestamps];
    private int _gpuTimingCount;

    private ImGuiRenderer? _imGui;
    private Vector2D<int> _framebufferSize;
    private SessionRequest _sessionRequest;

    public ScreenStack Screens { get; } = new();

    public GameSession? Session { get; private set; }

    /// <summary>
    ///     Starts a session at the next frame and replaces every screen with the loading screen.
    /// </summary>
    public void StartSession()
    {
        WgAssert.DebugIsOnRenderThread();

        _sessionRequest = SessionRequest.Start;
    }

    /// <summary>
    ///     Ends the session at the next frame and replaces every screen with the main menu.
    /// </summary>
    public void EndSession()
    {
        WgAssert.DebugIsOnRenderThread();

        _sessionRequest = SessionRequest.End;
    }

    public void Quit()
    {
        WgAssert.DebugIsOnRenderThread();

        G2Wnd.Window.Close();
    }

    protected override void BeforeStart()
    {
        WgAssert.InitRenderThread();

        _imGui = new ImGuiRenderer(Factory);
        _imGui.Initialize(
            G2Wnd.Window.FramebufferSize,
            () => G2Wnd.ClipboardText,
            text => G2Wnd.ClipboardText = text
        );

        Screens.Push(new MainMenuScreen(this));
    }

    protected override void OnResize(Vector2D<int> framebufferSize)
    {
        _framebufferSize = framebufferSize;
        Session?.OnResize(framebufferSize);
    }

    protected override void OnFrame(double deltaTime, Frame frame, InputSnapshot input)
    {
        ApplyRequests();

        var session = Session;
        var sessionHasFocus = session is { IsLoaded: true } && Screens.Count == 0;

        // The mouse steers the camera while the session has focus, so it must not also click the debug panels:
        SetImGuiMouseEnabled(!sessionHasFocus);
        _imGui!.Update((float)deltaTime, input, G2Wnd.Window.FramebufferSize);
        _tracker.Update();
        AverageGpuTimings();

        HandleEscape(input);

        session?.Update(frame, input, sessionHasFocus, Screens.PausesSession);
        Screens.Update(deltaTime);

        var visibleSession = Screens.HidesSession ? null : session;

        visibleSession?.Render(frame);

        DrawDockSpace();
        DrawTracker();
        visibleSession?.DrawDebugUi();
        Screens.Draw();

        SubmitUi(frame, visibleSession == null);
    }

    protected override void OnDispose()
    {
        Session?.Dispose();
        Session = null;
        _imGui?.Dispose();
    }

    private void ApplyRequests()
    {
        switch (_sessionRequest)
        {
            case SessionRequest.Start:
                Session?.Dispose();
                Session = new GameSession(this, _framebufferSize);
                Screens.Clear();
                Screens.Push(new LoadingScreen(this));
                break;
            case SessionRequest.End:
                Session?.Dispose();
                Session = null;
                Screens.Clear();
                Screens.Push(new MainMenuScreen(this));
                break;
        }

        _sessionRequest = SessionRequest.None;
        Screens.ApplyPending();
    }

    /// <summary>
    ///     Escape closes the top screen when that screen allows it, and pauses a loaded session that has no screen open.
    /// </summary>
    private void HandleEscape(InputSnapshot input)
    {
        // An active text field uses Escape to cancel its edit:
        if (!input.WasKeyPressed(G2Key.Escape) || ImGui.GetIO().WantCaptureKeyboard)
        {
            return;
        }

        var top = Screens.Top;

        if (top != null)
        {
            if (top.ClosesOnEscape)
            {
                Screens.Remove(top);
            }
        }
        else if (Session is { IsLoaded: true })
        {
            Screens.Push(new PauseScreen(this));
        }
    }

    private static void SetImGuiMouseEnabled(bool enabled)
    {
        var io = ImGui.GetIO();

        if (enabled)
        {
            io.ConfigFlags &= ~ImGuiConfigFlags.NoMouse;
        }
        else
        {
            io.ConfigFlags |= ImGuiConfigFlags.NoMouse;
        }
    }

    private static unsafe void DrawDockSpace()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.DockSpaceOverViewport(0U, (ImGuiViewport*) null, ImGuiDockNodeFlags.PassthruCentralNode, (ImGuiWindowClass*) null);
        ImGui.PopStyleColor();
    }

    private void DrawTracker()
    {
        if (ImGui.Begin("Tracker"))
        {
            ImGui.Text($"Frames: {_tracker.TotalFrames}/{_tracker.WindowFrames}");
            ImGui.Separator();
            ImGui.Text("Time (window):");
            ImGui.Text($"  Avg: {_tracker.AverageMilliseconds * 1000:F2}us");
            ImGui.Text($"  Min: {_tracker.MinimumMilliseconds * 1000:F2}us");
            ImGui.Text($"  Max: {_tracker.MaximumMilliseconds * 1000:F2}us");
            ImGui.Text($"  P50: {_tracker.MedianMilliseconds * 1000:F2}us");
            ImGui.Text($"  P95: {_tracker.P95Milliseconds * 1000:F2}us");
            ImGui.Text($"  P99: {_tracker.P99Milliseconds * 1000:F2}us");
            ImGui.Separator();
            ImGui.Text("GPU (averaged):");

            var total = 0.0;

            for (var index = 0; index < _gpuTimingCount; index++)
            {
                ImGui.Text($"  {_gpuLabels[index]}: {_gpuMilliseconds[index] * 1000:F0}us");
                total += _gpuMilliseconds[index];
            }

            ImGui.Text($"  Total: {total * 1000:F0}us");
        }

        ImGui.End();
    }

    private void AverageGpuTimings()
    {
        var timings = Context!.GpuTimings;

        for (var index = 0; index < timings.Length; index++)
        {
            var timing = timings[index];

            if (_gpuLabels[index] != timing.Label)
            {
                _gpuLabels[index] = timing.Label;
                _gpuMilliseconds[index] = timing.Milliseconds;
            }
            else
            {
                _gpuMilliseconds[index] += (timing.Milliseconds - _gpuMilliseconds[index]) * GpuTimingWeight;
            }
        }

        _gpuTimingCount = timings.Length;
    }

    /// <summary>
    ///     Draws ImGui and presents. The pass clears the image when no world was drawn under it.
    /// </summary>
    private void SubmitUi(Frame frame, bool clear)
    {
        using (var pass = frame.BeginRenderPass(new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = clear ? LoadOp.Clear : LoadOp.Load,
                    StoreOp = StoreOp.Store,
                    ClearValue = ClearColor,
                    FinalLayout = TextureLayout.Present
                }
            ]
        }))
        {
            _imGui!.Submit(frame, pass);
        }

        frame.WriteTimestamp("UI");
    }
}

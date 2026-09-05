using Graphics2.Windowing.Input;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Graphics2.Windowing;

public sealed class G2WindowDescription
{
    public string WindowTitle { get; init; } = "G2";
    public int Width { get; init; } = 800;
    public int Height { get; init; } = 600;
}

public sealed class G2Window : IDisposable
{
    public readonly IWindow Window;
    public readonly IVkSurface Surface;
    private readonly InputState _input;

    private G2Window(IWindow window, IVkSurface surface)
    {
        Window = window;
        Surface = surface;
        _input = new InputState(window);
    }

    public G2CursorMode CursorMode
    {
        get => _input.CursorMode;
        set => _input.CursorMode = value;
    }

    public string? ClipboardText
    {
        get => _input.ClipboardText;
        set => _input.ClipboardText = value;
    }

    public InputSnapshot BuildInputSnapshot() => _input.BuildSnapshot();

    public static G2Window Create(G2WindowDescription description)
    {
        var options = WindowOptions.DefaultVulkan with
        {
            Title = description.WindowTitle,
            Size = new Vector2D<int>(description.Width, description.Height)
        };

        var window = Silk.NET.Windowing.Window.Create(options);
        window.Initialize();

        var surface = window.VkSurface;

        if (surface == null)
        {
            throw new WndException("Vulkan surface not present in created window");
        }

        return new G2Window(window, surface);
    }

    public void Dispose()
    {
        _input.Dispose();
        Window.Dispose();
    }
}

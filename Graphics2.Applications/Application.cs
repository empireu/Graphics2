using Graphics2.Core.Bootstrap;
using Graphics2.Windowing;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Graphics2.Applications;

public sealed class ApplicationDescription
{
    public G2WindowDescription Window { get; init; } = new();

    public VkInstanceDescription Instance { get; init; } = new() { UseValidationLayers = true };
}

public abstract unsafe class Application : IDisposable
{
    public readonly VkInstance Instance;
    public readonly G2Window Window;
    public readonly SurfaceKHR Surface;
    public readonly VkDevice Device;
    private readonly KhrSurface _khrSurface;

    private Vector2D<int> _lastFramebufferSize;
    private double _lastTime;
    private bool _disposed;
    
    protected Application(ApplicationDescription description)
    {
        Window = G2Window.Create(description.Window);
        IReadOnlyList<string> requiredExtensions = ReadRequiredExtensions(Window.Surface);
        
        Instance = VkInstance.Create(description.Instance with
        {
            AdditionalExtensions = description.Instance.AdditionalExtensions
            .Concat(requiredExtensions)
            .ToList()
        });
        
        var surfaceHandle = Window.Surface.Create<AllocationCallbacks>(new VkHandle(Instance.Handle.Handle), null);
        Surface = new SurfaceKHR(surfaceHandle.Handle);
        Device = VkDevice.Create(Instance, Surface, new VkDeviceDescription());
        if (!Instance.Vk.TryGetInstanceExtension<KhrSurface>(Instance.Handle, out var khrSurface))
        {
            throw new VkBootstrapException("Failed to get KHR surface extension");
        }

        _khrSurface = khrSurface;
    }

    /// <summary>
    ///     Called with the current framebuffer size before the first frame, and on resize.
    ///     Size is in pixels, as required by the swapchain extent.
    ///     Not called while the window is minimized.
    /// </summary>
    protected abstract void OnResize(Vector2D<int> framebufferSize);

    protected abstract void OnFrame(double deltaTime);

    public void Run()
    {
        _lastTime = Window.Window.Time;
        
        while (!Window.Window.IsClosing)
        {
            Window.Window.DoEvents();
            PollResize();
            var now = Window.Window.Time;
            OnFrame(now - _lastTime);
            _lastTime = now;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        
        Window.Dispose();
        Device.Dispose();
        _khrSurface.DestroySurface(Instance.Handle, Surface, null);
        Instance.Dispose();
        
        GC.SuppressFinalize(this);
    }

    private void PollResize()
    {
        var size = Window.Window.FramebufferSize;
        if (size == _lastFramebufferSize)
        {
            return;
        }

        _lastFramebufferSize = size;
        
        if (size is { X: > 0, Y: > 0 })
        {
            OnResize(size);
        }
    }

    private static List<string> ReadRequiredExtensions(IVkSurface surface)
    {
        var ptr = surface.GetRequiredExtensions(out var count);
        var extensions = new List<string>((int)count);
        for (var i = 0; i < count; i++)
        {
            extensions.Add(SilkMarshal.PtrToString((nint)ptr[i]) ?? string.Empty);
        }

        return extensions;
    }
}

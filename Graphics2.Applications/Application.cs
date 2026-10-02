using Graphics2.Core.Bootstrap;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Windowing;
using Graphics2.Windowing.Input;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Diagnostics;
using System.Threading;

namespace Graphics2.Applications;

public sealed class ApplicationDescription
{
    public G2WindowDescription Window { get; init; } = new();

    public VkInstanceDescription Instance { get; init; } = new() { UseValidationLayers = true };

    public VkSwapchainDescription Swapchain { get; init; } = new();

    public uint FramesInFlight { get; init; } = 2;
}

public abstract unsafe class Application : IDisposable
{
    public readonly VkInstance Instance;
    public readonly G2Window G2Wnd;
    public readonly SurfaceKHR Surface;
    public readonly VkDevice Device;
    public readonly VulkanResourceFactory Factory;
    
    private readonly KhrSurface _khrSurface;
    private Vector2D<int> _lastFramebufferSize;
    private long _lastTimestamp;
    private bool _disposed;

    protected Application(ApplicationDescription description)
    {
        G2Wnd = G2Window.Create(description.Window);
        IReadOnlyList<string> requiredExtensions = ReadRequiredExtensions(G2Wnd.Surface);

        Instance = VkInstance.Create(description.Instance with
        {
            AdditionalExtensions = description.Instance.AdditionalExtensions
            .Concat(requiredExtensions)
            .ToList()
        });

        var surfaceHandle = G2Wnd.Surface.Create<AllocationCallbacks>(new VkHandle(Instance.Handle.Handle), null);
        Surface = new SurfaceKHR(surfaceHandle.Handle);
        Device = VkDevice.Create(Instance, Surface, new VkDeviceDescription());
        Factory = new VulkanResourceFactory(Device);
       
        if (!Instance.Vk.TryGetInstanceExtension<KhrSurface>(Instance.Handle, out var khrSurface))
        {
            throw new VkBootstrapException("Failed to get KHR surface extension");
        }

        _khrSurface = khrSurface;

        var initialSize = G2Wnd.Window.FramebufferSize;
        _lastFramebufferSize = initialSize;

        if (initialSize is { X: > 0, Y: > 0 })
        {
            Context = RenderContext.Create(
                Device,
                Factory,
                Surface,
                ToExtent(initialSize),
                description.Swapchain,
                description.FramesInFlight
            );
        }
    }

    public RenderContext? Context { get; private set; }

    /// <summary>
    ///     Called with the current framebuffer size before the first frame, and on resize.
    ///     Size is in pixels, as required by the swapchain extent.
    ///     Not called while the window is minimized.
    /// </summary>
    protected abstract void OnResize(Vector2D<int> framebufferSize);

    protected abstract void OnFrame(double deltaTime, Frame frame, InputSnapshot input);

    protected virtual void BeforeStart() { }
    
    protected virtual void OnStart() { }

    protected virtual void OnDispose() { }

    public void Run()
    {
        BeforeStart();
        
        if (_lastFramebufferSize is { X: > 0, Y: > 0 })
        {
            OnResize(_lastFramebufferSize);
        }

        OnStart();

        _lastTimestamp = Stopwatch.GetTimestamp();

        while (!G2Wnd.Window.IsClosing)
        {
            G2Wnd.Window.DoEvents();

            // One timestamp per frame, taken when the input is read in, gives both the frame's delta time and the instant its input describes:
            var timestamp = Stopwatch.GetTimestamp();

            PollResize();

            if (Context == null || _lastFramebufferSize is not { X: > 0, Y: > 0 })
            {
                _lastTimestamp = timestamp;
                Thread.Sleep(1);
                continue;
            }

            var frame = Context.BeginFrame();
            var input = G2Wnd.BuildInputSnapshot(timestamp);
            OnFrame(Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds, frame, input);
            _lastTimestamp = timestamp;
            Context.EndFrame(frame);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Device.WaitIdle();
        OnDispose();
        Context?.Dispose();
        Factory.Dispose();
        G2Wnd.Dispose();
        Device.Dispose();
        _khrSurface.DestroySurface(Instance.Handle, Surface, null);
        Instance.Dispose();

        GC.SuppressFinalize(this);
    }

    private void PollResize()
    {
        var size = G2Wnd.Window.FramebufferSize;
        if (size == _lastFramebufferSize)
        {
            return;
        }

        _lastFramebufferSize = size;

        if (size is { X: > 0, Y: > 0 })
        {
            var extent = ToExtent(size);

            if (Context == null)
            {
                Context = RenderContext.Create(
                    Device,
                    Factory,
                    Surface,
                    extent,
                    new VkSwapchainDescription()
                );
            }
            else
            {
                Context.Recreate(extent);
            }

            OnResize(size);
        }
    }

    private static Extent2D ToExtent(Vector2D<int> size) => new()
    {
        Width = (uint)size.X,
        Height = (uint)size.Y
    };

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

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Graphics2.Core.Bootstrap;

public sealed class VkSwapchainDescription
{
    public bool VSync { get; init; } = true;

    public Format PreferredFormat { get; init; } = Format.B8G8R8A8Srgb;

    public uint DesiredImageCount { get; init; } = 3;
}

public sealed unsafe class VkSwapchain : IDisposable
{
    private readonly VkDevice _device;
    private readonly SurfaceKHR _surface;
    private readonly KhrSurface _khrSurface;
    private readonly KhrSwapchain _khrSwapchain;
    private readonly VkSwapchainDescription _description;

    private SwapchainKHR _handle;
    private Image[] _images = [];
    private ImageView[] _views = [];
    private int _disposed;

    private VkSwapchain(
        VkDevice device,
        SurfaceKHR surface,
        KhrSurface khrSurface,
        KhrSwapchain khrSwapchain,
        VkSwapchainDescription description)
    {
        _device = device;
        _surface = surface;
        _khrSurface = khrSurface;
        _khrSwapchain = khrSwapchain;
        _description = description;
    }

    public SwapchainKHR Handle => _handle;
    
    public uint ImageCount => (uint)_images.Length;
    
    public Format Format { get; private set; }
    
    public Extent2D Extent { get; private set; }

    public IReadOnlyList<ImageView> Views => _views;

    public IReadOnlyList<Image> Images => _images;
    
    public static VkSwapchain Create(
        VkDevice device,
        SurfaceKHR surface,
        Extent2D extent,
        VkSwapchainDescription description)
    {
        var vk = device.Instance.Vk;

        if (!vk.TryGetInstanceExtension<KhrSurface>(device.Instance.Handle, out var khrSurface))
        {
            throw new VkBootstrapException("Failed to get KHR surface extension");
        }

        if (!vk.TryGetDeviceExtension<KhrSwapchain>(device.Instance.Handle, device.Device, out var khrSwapchain))
        {
            throw new VkBootstrapException("Failed to get KHR swapchain extension");
        }

        var swapchain = new VkSwapchain(
            device,
            surface,
            khrSurface,
            khrSwapchain,
            description
        );

        swapchain.Build(extent);
        
        return swapchain;
    }

    public void Recreate(Extent2D extent)
    {
        _device.Instance.Vk.DeviceWaitIdle(_device.Device);
        DestroySwapchainDerived();
        Build(extent);
    }

    /// <summary>
    ///     Acquires the next swapchain image.
    ///     Returns <see cref="Result.Success"/>, <see cref="Result.SuboptimalKhr"/> or <see cref="Result.ErrorOutOfDateKhr"/> (caller must handle the bad results by recreating the swapchain).
    ///     The <see cref="imageAvailable"/> semaphore is signaled by the acquire and must be waited on before rendering to the image.
    /// </summary>
    public Result AcquireNextImage(Semaphore imageAvailable, out uint imageIndex)
    {
        uint index = 0;
        var result = _khrSwapchain.AcquireNextImage(
            _device.Device,
            _handle,
            ulong.MaxValue,
            imageAvailable,
            default,
            ref index
        );

        imageIndex = index;
        return result;
    }

    /// <summary>
    ///     Presents a finished image. The <see cref="renderFinished"/> semaphore must have been signaled by the command buffer submission that wrote the image.
    /// </summary>
    public Result QueuePresent(Semaphore renderFinished, uint imageIndex)
    {
        var swapchain = _handle;
        var waitSemaphore = renderFinished;
        var index = imageIndex;

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &index
        };

        return _khrSwapchain.QueuePresent(_device.Queue, &presentInfo);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        DestroySwapchainDerived();
    }

    private void Build(Extent2D extent)
    {
        SurfaceCapabilitiesKHR capabilities;
        if (_khrSurface.GetPhysicalDeviceSurfaceCapabilities(_device.Info.PhysicalDevice, _surface, &capabilities) != Result.Success)
        {
            throw new VkBootstrapException("Failed to get surface capabilities");
        }

        var formats = QueryFormats();
        var presentModes = QueryPresentModes();

        var surfaceFormat = ChooseFormat(formats);
        var presentMode = ChoosePresentMode(presentModes);
        var swapchainExtent = ChooseExtent(capabilities, extent);
        var minImageCount = ChooseMinImageCount(capabilities);

        Format = surfaceFormat.Format;
        Extent = swapchainExtent;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = minImageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default
        };

        if (_khrSwapchain.CreateSwapchain(_device.Device, in createInfo, null, out var handle) != Result.Success)
        {
            throw new VkBootstrapException("Failed to create swapchain");
        }

        _handle = handle;

        uint imageCount;
        _khrSwapchain.GetSwapchainImages(_device.Device, _handle, &imageCount, null);

        _images = new Image[imageCount];
        fixed (Image* ptr = _images)
        {
            _khrSwapchain.GetSwapchainImages(_device.Device, _handle, &imageCount, ptr);
        }

        _views = new ImageView[_images.Length];
        
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var i = 0; i < _images.Length; i++)
        {
            var viewCreateInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                Components =
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            if (_device.Instance.Vk.CreateImageView(_device.Device, in viewCreateInfo, null, out _views[i]) != Result.Success)
            {
                throw new VkBootstrapException("Failed to create swapchain image view");
            }
        }
    }

    private void DestroySwapchainDerived()
    {
        foreach (var view in _views)
        {
            _device.Instance.Vk.DestroyImageView(_device.Device, view, null);
        }

        _views = [];

        if (_handle.Handle != 0)
        {
            _khrSwapchain.DestroySwapchain(_device.Device, _handle, null);
            _handle = default;
        }

        _images = [];
    }

    private SurfaceFormatKHR[] QueryFormats()
    {
        uint count;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_device.Info.PhysicalDevice, _surface, &count, null);

        if (count == 0)
        {
            throw new VkBootstrapException("Surface reports zero formats");
        }

        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* ptr = formats)
        {
            if (_khrSurface.GetPhysicalDeviceSurfaceFormats(_device.Info.PhysicalDevice, _surface, &count, ptr) != Result.Success)
            {
                throw new VkBootstrapException("Failed to get surface formats");
            }
        }

        return formats;
    }

    private PresentModeKHR[] QueryPresentModes()
    {
        uint count;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_device.Info.PhysicalDevice, _surface, &count, null);

        if (count == 0)
        {
            throw new VkBootstrapException("Surface reports zero present modes");
        }

        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* ptr = modes)
        {
            if (_khrSurface.GetPhysicalDeviceSurfacePresentModes(_device.Info.PhysicalDevice, _surface, &count, ptr) != Result.Success)
            {
                throw new VkBootstrapException("Failed to get surface present modes");
            }
        }

        return modes;
    }

    private SurfaceFormatKHR ChooseFormat(SurfaceFormatKHR[] formats)
    {
        foreach (var format in formats)
        {
            if (format.Format == _description.PreferredFormat && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return format;
            }
        }

        return formats[0];
    }

    private PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes)
    {
        if (_description.VSync)
        {
            return PresentModeKHR.FifoKhr;
        }

        if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
        {
            return PresentModeKHR.MailboxKhr;
        }

        return PresentModeKHR.FifoKhr;
    }

    private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities, Extent2D desiredExtent)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        var width = Math.Clamp(
            desiredExtent.Width,
            capabilities.MinImageExtent.Width,
            capabilities.MaxImageExtent.Width
        );
        
        var height = Math.Clamp(
            desiredExtent.Height,
            capabilities.MinImageExtent.Height,
            capabilities.MaxImageExtent.Height
        );

        return new Extent2D
        {
            Width = width,
            Height = height
        };
    }

    private uint ChooseMinImageCount(SurfaceCapabilitiesKHR caps)
    {
        var count = Math.Max(_description.DesiredImageCount, caps.MinImageCount);

        if (caps.MaxImageCount > 0 && count > caps.MaxImageCount)
        {
            count = caps.MaxImageCount;
        }

        return count;
    }
}
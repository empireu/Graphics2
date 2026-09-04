using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

public enum TextureLayout
{
    /// <summary>
    ///     Color attachment optimal. Image is written by a render pass.
    /// </summary>
    ColorAttachment,
    /// <summary>
    ///     Shader read only optimal. Image is sampled by a shader.
    /// </summary>
    ShaderRead,
    /// <summary>
    ///     Present source. Image is ready for presentation.
    /// </summary>
    Present,
    /// <summary>
    ///     Don't care. Contents are discarded (layout is undefined).
    /// </summary>
    None
}

public class VkTexture
{
    internal readonly Image Image;
    internal readonly ImageView View;
    internal ImageLayout CurrentLayout;

    internal VkTexture(Image image, ImageView view, ImageLayout initialLayout)
    {
        Image = image;
        View = view;
        CurrentLayout = initialLayout;
    }
}

public sealed unsafe class Texture : VkTexture, IDisposable
{
    private readonly VkDevice _device;
    private readonly BlockAllocator<VulkanMemory> _allocator;
    private readonly Allocation _allocation;
    private int _disposed;

    public readonly uint Width;
    public readonly uint Height;
    public readonly TextureFormat Format;
    public readonly TextureUsage Usage;

    internal Texture(
        VkDevice device,
        BlockAllocator<VulkanMemory> allocator,
        Image image,
        ImageView view,
        Allocation allocation,
        uint width,
        uint height,
        TextureFormat format,
        TextureUsage usage)
        : base(image, view, ImageLayout.Undefined)
    {
        _device = device;
        _allocator = allocator;
        _allocation = allocation;
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;
        vk.DeviceWaitIdle(_device.Device);
        vk.DestroyImageView(_device.Device, View, null);
        vk.DestroyImage(_device.Device, Image, null);
        _allocator.Free(_allocation);
    }
}
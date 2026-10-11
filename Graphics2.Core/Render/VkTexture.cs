using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;
using Graphics2.Core.Resources;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

public enum TextureLayout
{
    /// <summary>
    ///     Don't care. Contents are discarded (layout is undefined).
    /// </summary>
    None,
    /// <summary>
    ///     Color attachment optimal. Image is written by a render pass.
    /// </summary>
    ColorAttachment,
    /// <summary>
    ///     Depth attachment optimal. Image is used as a depth/stencil attachment.
    /// </summary>
    DepthAttachment,
    /// <summary>
    ///     Shader read only optimal. Image is sampled by a shader.
    /// </summary>
    ShaderRead,
    /// <summary>
    ///     Present source. Image is ready for presentation.
    /// </summary>
    Present
}

internal static class TextureLayoutMapping
{
    /// <summary>
    ///     The Vulkan layout a tracked layout maps to.
    /// </summary>
    internal static ImageLayout ToVk(this TextureLayout layout) => layout switch
    {
        TextureLayout.ColorAttachment => ImageLayout.ColorAttachmentOptimal,
        TextureLayout.ShaderRead => ImageLayout.ShaderReadOnlyOptimal,
        TextureLayout.Present => ImageLayout.PresentSrcKhr,
        TextureLayout.None => ImageLayout.Undefined,
        TextureLayout.DepthAttachment => ImageLayout.DepthAttachmentOptimal,
        _ => ImageLayout.ColorAttachmentOptimal
    };
}

public class VkTexture
{
    internal readonly Image Image;
    internal readonly ImageView View;

    /// <summary>
    ///     How many axes the image has, which is what a shader's declared resource shape has to agree with.
    /// </summary>
    public readonly TextureType Type;

    /// <summary>
    ///     The aspects a barrier on the whole image has to name, which follow from its format.
    /// </summary>
    internal readonly ImageAspectFlags Aspect;

    internal ImageLayout CurrentLayout;

    internal VkTexture(Image image, ImageView view, TextureType type, ImageAspectFlags aspect, ImageLayout initialLayout)
    {
        Image = image;
        View = view;
        Type = type;
        Aspect = aspect;
        CurrentLayout = initialLayout;
    }

    /// <summary>
    ///     Every mip level and array layer of an image, which is what a barrier names, because a texture tracks one layout for all of them.
    /// </summary>
    internal static ImageSubresourceRange AllSubresources(ImageAspectFlags aspect)
    {
        return new ImageSubresourceRange
        {
            AspectMask = aspect,
            BaseMipLevel = 0,
            LevelCount = Vk.RemainingMipLevels,
            BaseArrayLayer = 0,
            LayerCount = Vk.RemainingArrayLayers
        };
    }
}

public sealed unsafe class Texture : VkTexture, IDeviceResource
{
    private readonly VkDevice _device;
    private readonly BlockAllocator<VulkanMemory> _allocator;
    private readonly Allocation _allocation;
    private int _disposed;

    public readonly uint Width;
    public readonly uint Height;
    public readonly uint Depth;
    public readonly uint MipLevels;
    public readonly uint ArrayLayers;
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
        uint depth,
        uint mipLevels,
        uint arrayLayers,
        TextureFormat format,
        TextureUsage usage,
        TextureType type,
        ImageAspectFlags aspect)
        : base(image, view, type, aspect, ImageLayout.Undefined)
    {
        _device = device;
        _allocator = allocator;
        _allocation = allocation;
        Width = width;
        Height = height;
        Depth = depth;
        MipLevels = mipLevels;
        ArrayLayers = arrayLayers;
        Format = format;
        Usage = usage;
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _device.WaitIdle();
        
        Destroy();
    }

    public void Destroy()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;

        vk.DestroyImageView(_device.Device, View, null);
        vk.DestroyImage(_device.Device, Image, null);
        _allocator.Free(_allocation);
    }
}
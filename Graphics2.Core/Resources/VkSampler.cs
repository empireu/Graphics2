using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Resources;

public enum FilterMode
{
    Nearest,
    Linear
}

public enum SamplerAddressMode
{
    Repeat,
    MirroredRepeat,
    ClampToEdge,
    ClampToBorder
}

public sealed class SamplerDescription
{
    public FilterMode MinFilter { get; init; } = FilterMode.Linear;
    public FilterMode MagFilter { get; init; } = FilterMode.Linear;
    public SamplerAddressMode AddressModeU { get; init; } = SamplerAddressMode.Repeat;
    public SamplerAddressMode AddressModeV { get; init; } = SamplerAddressMode.Repeat;
    public SamplerAddressMode AddressModeW { get; init; } = SamplerAddressMode.Repeat;
    public bool AnisotropyEnabled { get; init; } = true;
    public float MaxAnisotropy { get; init; } = 16.0f;
}

public sealed unsafe class VkSampler : IDisposable
{
    private readonly VkDevice _device;
    private int _disposed;

    internal Sampler Handle;

    private VkSampler(VkDevice device, Sampler handle)
    {
        _device = device;
        Handle = handle;
    }

    internal static VkSampler Create(VkDevice device, SamplerDescription description)
    {
        var vk = device.Instance.Vk;

        var createInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = description.MagFilter == FilterMode.Linear ? Filter.Linear : Filter.Nearest,
            MinFilter = description.MinFilter == FilterMode.Linear ? Filter.Linear : Filter.Nearest,
            AddressModeU = ToVkAddressMode(description.AddressModeU),
            AddressModeV = ToVkAddressMode(description.AddressModeV),
            AddressModeW = ToVkAddressMode(description.AddressModeW),
            AnisotropyEnable = description.AnisotropyEnabled,
            MaxAnisotropy = description.MaxAnisotropy,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            CompareOp = Silk.NET.Vulkan.CompareOp.Never,
            MipmapMode = SamplerMipmapMode.Linear
        };

        if (vk.CreateSampler(device.Device, in createInfo, null, out var handle) != Result.Success)
        {
            throw new VkAllocationException("Failed to create sampler.");
        }

        return new VkSampler(device, handle);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _device.Instance.Vk.DestroySampler(_device.Device, Handle, null);
    }

    private static Silk.NET.Vulkan.SamplerAddressMode ToVkAddressMode(SamplerAddressMode mode) => mode switch
    {
        SamplerAddressMode.Repeat => Silk.NET.Vulkan.SamplerAddressMode.Repeat,
        SamplerAddressMode.MirroredRepeat => Silk.NET.Vulkan.SamplerAddressMode.MirroredRepeat,
        SamplerAddressMode.ClampToEdge => Silk.NET.Vulkan.SamplerAddressMode.ClampToEdge,
        SamplerAddressMode.ClampToBorder => Silk.NET.Vulkan.SamplerAddressMode.ClampToBorder,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Resources;

public enum ShaderStage
{
    Vertex,
    Fragment,
    Compute
}

public sealed class ShaderDescription
{
    public required ReadOnlyMemory<byte> SpirV { get; init; }
    
    public required ShaderStage Stage { get; init; }
    
    public string EntryPoint { get; init; } = "main";
}

public sealed unsafe class VkShader : IDisposable
{
    private readonly VkDevice _device;
    private int _disposed;

    internal ShaderModule Handle;

    public ShaderStage Stage { get; }
    public string EntryPoint { get; }

    internal VkShader(VkDevice device, ShaderModule handle, ShaderStage stage, string entryPoint)
    {
        _device = device;
        Handle = handle;
        Stage = stage;
        EntryPoint = entryPoint;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _device.Instance.Vk.DestroyShaderModule(_device.Device, Handle, null);
    }
}
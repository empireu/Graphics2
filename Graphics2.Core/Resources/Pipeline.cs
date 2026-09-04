using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Resources;

public enum CompareOp
{
    Never,
    Less,
    Equal,
    LessOrEqual,
    Greater,
    NotEqual,
    GreaterOrEqual,
    Always
}

public enum PrimitiveTopology
{
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
    TriangleFan
}

public enum CullMode
{
    None,
    Front,
    Back
}

public enum FrontFace
{
    CounterClockwise,
    Clockwise
}

public enum BlendFactor
{
    Zero,
    One,
    SrcAlpha,
    OneMinusSrcAlpha,
    DstAlpha,
    OneMinusDstAlpha,
    SrcColor,
    OneMinusSrcColor,
    DstColor,
    OneMinusDstColor
}

public readonly struct BlendState(bool enabled, BlendFactor srcFactor, BlendFactor dstFactor)
{
    public readonly bool Enabled = enabled;
    public readonly BlendFactor SrcFactor = srcFactor;
    public readonly BlendFactor DstFactor = dstFactor;

    public static readonly BlendState None = new(false, BlendFactor.One, BlendFactor.Zero);
    public static readonly BlendState AlphaBlend = new(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);
    public static readonly BlendState Additive = new(true, BlendFactor.SrcAlpha, BlendFactor.One);
}

public sealed class PipelineDescription
{
    public required string Source { get; init; }
    public required string VertexEntryPoint { get; init; }
    public string? FragmentEntryPoint { get; init; }

    public required TextureFormat[] ColorAttachments { get; init; }
    public TextureFormat? DepthAttachment { get; init; }

    public bool DepthTest { get; init; } = true;
    public bool DepthWrite { get; init; } = true;
    public CompareOp DepthCompare { get; init; } = CompareOp.Less;

    public PrimitiveTopology Topology { get; init; } = PrimitiveTopology.TriangleList;
    public CullMode CullMode { get; init; } = CullMode.Back;
    public FrontFace FrontFace { get; init; } = FrontFace.CounterClockwise;
    public bool Wireframe { get; init; }

    public BlendState[]? Blends { get; init; }
}

public sealed unsafe class Pipeline : IDisposable
{
    private readonly VkDevice _device;
    private readonly VkShader _vertexShader;
    private readonly VkShader? _fragmentShader;
    private readonly DescriptorSetLayout[] _descriptorSetLayouts;
    private int _disposed;

    internal Silk.NET.Vulkan.Pipeline Handle;
    internal PipelineLayout Layout;

    public ShaderReflection Reflection { get; }

    internal Pipeline(
        VkDevice device,
        Silk.NET.Vulkan.Pipeline handle,
        PipelineLayout layout,
        DescriptorSetLayout[] descriptorSetLayouts,
        VkShader vertexShader,
        VkShader? fragmentShader,
        ShaderReflection reflection)
    {
        _device = device;
        Handle = handle;
        Layout = layout;
        _descriptorSetLayouts = descriptorSetLayouts;
        _vertexShader = vertexShader;
        _fragmentShader = fragmentShader;
        Reflection = reflection;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;
        vk.DeviceWaitIdle(_device.Device);

        vk.DestroyPipeline(_device.Device, Handle, null);
        vk.DestroyPipelineLayout(_device.Device, Layout, null);

        foreach (var setLayout in _descriptorSetLayouts)
        {
            vk.DestroyDescriptorSetLayout(_device.Device, setLayout, null);
        }

        _vertexShader.Dispose();
        _fragmentShader?.Dispose();
    }
}

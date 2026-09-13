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

    public VertexLayout? VertexLayout { get; init; }
    public PushConstantLayout? PushConstantLayout { get; init; }
    public Dictionary<string, SsboLayout>? SsboLayouts { get; init; }
}

public sealed unsafe class Pipeline : IDeviceResource
{
    private readonly VkDevice _device;
    private readonly VkShader _vertexShader;
    private readonly VkShader? _fragmentShader;
    private readonly Dictionary<string, ShaderParameterReflection> _parametersByName;
    private int _disposed;

    internal Silk.NET.Vulkan.Pipeline Handle;
    internal PipelineLayout Layout;
    internal readonly DescriptorSetLayout[] DescriptorSetLayouts;
    internal readonly uint PushConstantSize;
    internal readonly ShaderStageFlags PushConstantStageFlags;
    public readonly ShaderReflection Reflection;

    internal Pipeline(
        VkDevice device,
        Silk.NET.Vulkan.Pipeline handle,
        PipelineLayout layout,
        DescriptorSetLayout[] descriptorSetLayouts,
        VkShader vertexShader,
        VkShader? fragmentShader,
        ShaderReflection reflection,
        uint pushConstantSize,
        ShaderStageFlags pushConstantStageFlags)
    {
        _device = device;
        Handle = handle;
        Layout = layout;
        DescriptorSetLayouts = descriptorSetLayouts;
        _vertexShader = vertexShader;
        _fragmentShader = fragmentShader;
        Reflection = reflection;
        PushConstantSize = pushConstantSize;
        PushConstantStageFlags = pushConstantStageFlags;

        _parametersByName = new Dictionary<string, ShaderParameterReflection>(reflection.Parameters.Length);
        foreach (var parameter in reflection.Parameters)
        {
            _parametersByName[parameter.Name] = parameter;
        }
    }

    internal bool TryGetParameter(string name, out ShaderParameterReflection parameter) => _parametersByName.TryGetValue(name, out parameter);

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

        vk.DestroyPipeline(_device.Device, Handle, null);
        vk.DestroyPipelineLayout(_device.Device, Layout, null);

        foreach (var setLayout in DescriptorSetLayouts)
        {
            vk.DestroyDescriptorSetLayout(_device.Device, setLayout, null);
        }

        _vertexShader.Destroy();
        _fragmentShader?.Destroy();
    }
}

namespace Graphics2.Core.Resources;

public enum VertexAttributeFormat
{
    Float,
    Float2,
    Float3,
    Float4,
    Int,
    Int2,
    Int3,
    Int4,
    UInt,
    UInt2,
    UInt3,
    UInt4
}

public readonly struct VertexAttributeReflection(string name, uint location, VertexAttributeFormat format, uint offset)
{
    public readonly string Name = name;
    public readonly uint Location = location;
    public readonly VertexAttributeFormat Format = format;
    public readonly uint Offset = offset;
}

public readonly struct EntryPointReflection(string name, ShaderStage stage, VertexAttributeReflection[] vertexInputs)
{
    public readonly string Name = name;
    public readonly ShaderStage Stage = stage;
    public readonly VertexAttributeReflection[] VertexInputs = vertexInputs;
}

public enum ShaderParameterKind
{
    UniformBuffer,
    StorageBuffer,
    SampledImage,
    Sampler,
    StorageImage
}

public readonly struct ShaderParameterReflection(string name, uint bindingIndex, uint bindingSpace, ShaderParameterKind kind)
{
    public readonly string Name = name;
    public readonly uint BindingIndex = bindingIndex;
    public readonly uint BindingSpace = bindingSpace;
    public readonly ShaderParameterKind Kind = kind;
}

public sealed class ShaderReflection
{
    public required EntryPointReflection[] EntryPoints { get; init; }
    
    public required ShaderParameterReflection[] Parameters { get; init; }
}
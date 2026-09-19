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
    UInt4,
    R8G8B8A8Unorm
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

public readonly struct SsboFieldReflection(string name, uint offset)
{
    public readonly string Name = name;
    public readonly uint Offset = offset;
}

public readonly struct ShaderParameterReflection(
    string name,
    uint bindingIndex,
    uint bindingSpace,
    ShaderParameterKind kind,
    uint elementCount,
    uint elementStride,
    SsboFieldReflection[]? elementFields,
    TextureType? textureType)
{
    public readonly string Name = name;
    public readonly uint BindingIndex = bindingIndex;
    public readonly uint BindingSpace = bindingSpace;
    public readonly ShaderParameterKind Kind = kind;

    /// <summary>
    ///     Number of elements in the parameter's array.
    ///     <c>1</c> for non-array parameters, <c>N</c> for fixed-size arrays (e.g. <c>Texture2D textures[1024]</c>), <c>0</c> for unsized arrays (unsupported by the pipeline creation).
    /// </summary>
    public readonly uint ElementCount = elementCount;

    /// <summary>
    ///     For sampled and storage images: how many axes the shader declared, which the bound image has to match.
    ///     <c>null</c> for every other kind of parameter, and for image shapes whose axes this does not describe, such as a cube or a buffer.
    /// </summary>
    public readonly TextureType? TextureType = textureType;

    /// <summary>
    ///     For storage buffers: the per-element stride in bytes as the shader sees it (std430 layout).
    ///     <c>0</c> for non-storage-buffer parameters.
    /// </summary>
    public readonly uint ElementStride = elementStride;

    /// <summary>
    ///     For storage buffers: the fields of the element struct with their offsets as the shader sees it.
    ///     <c>null</c> for non-storage-buffer parameters or scalar element types.
    /// </summary>
    public readonly SsboFieldReflection[]? ElementFields = elementFields;
}

public readonly struct PushConstantFieldReflection(string name, uint offset)
{
    public readonly string Name = name;
    public readonly uint Offset = offset;
}

public readonly struct PushConstantReflection(string name, uint size, PushConstantFieldReflection[] fields)
{
    public readonly string Name = name;
    public readonly uint Size = size;
    public readonly PushConstantFieldReflection[] Fields = fields;
}

public sealed class ShaderReflection
{
    public required EntryPointReflection[] EntryPoints { get; init; }
    
    public required ShaderParameterReflection[] Parameters { get; init; }

    public required PushConstantReflection[] PushConstants { get; init; }
}
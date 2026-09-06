using SlangShaderSharp;
using SlangEntryPointReflection = SlangShaderSharp.EntryPointReflection;
using SlangShaderReflection = SlangShaderSharp.ShaderReflection;

// ReSharper disable BitwiseOperatorOnEnumWithoutFlags

namespace Graphics2.Core.Resources;

public sealed class CompiledShader
{
    public required byte[] VertexSpirV { get; init; }
    public required byte[]? FragmentSpirV { get; init; }
    public required ShaderReflection Reflection { get; init; }
}

public sealed class ShaderCompiler : IDisposable
{
    private int _disposed;

    private IGlobalSession Session
    {
        get
        {
            if (field != null)
            {
                return field;
            }

            if (!Slang.CreateGlobalSession(Slang.ApiVersion, out var session).Succeeded)
            {
                throw new VkException("Failed to create Slang global session.");
            }

            field = session;
            return field;
        }
    }

    public CompiledShader Compile(string source, string vertexEntryPoint, string? fragmentEntryPoint = null)
    {
        var sessionDesc = new SessionDesc
        {
            Targets =
            [
                new TargetDesc
                {
                    Format = SlangCompileTarget.Spirv
                }
            ]
        };

        if (!Session.CreateSession(sessionDesc, out var session).Succeeded)
        {
            throw new VkException("Failed to create Slang session.");
        }

        return Compile(session, source, vertexEntryPoint, fragmentEntryPoint);
    }

    private static CompiledShader Compile(ISession session, string source, string vertexEntryPoint,
        string? fragmentEntryPoint)
    {
        var module = session.LoadModuleFromSource("shader", "shader.slang", Slang.CreateBlob(source), out var moduleDiagnostics);
        if (module == null)
        {
            throw new VkException($"Failed to load Slang module: \"{moduleDiagnostics?.AsString}\"");
        }

        if (!module.FindEntryPointByName(vertexEntryPoint, out var vsEntryPoint).Succeeded)
        {
            throw new VkException($"Failed to find vertex entry point \"{vertexEntryPoint}\".");
        }

        IEntryPoint? fsEntryPoint = null;
        if (fragmentEntryPoint != null)
        {
            if (!module.FindEntryPointByName(fragmentEntryPoint, out fsEntryPoint).Succeeded)
            {
                throw new VkException($"Failed to find fragment entry point \"{fragmentEntryPoint}\".");
            }
        }

        var components = fsEntryPoint != null
            ? new IComponentType[] { module, vsEntryPoint, fsEntryPoint }
            : new IComponentType[] { module, vsEntryPoint };

        if (!session.CreateCompositeComponentType(components, out var composedProgram, out var composeDiagnostic).Succeeded)
        {
            throw new VkException($"Failed to compose Slang program: {composeDiagnostic?.AsString}");
        }

        if (!composedProgram.Link(out var linkedProgram, out var linkDiag).Succeeded)
        {
            throw new VkException($"Failed to link Slang program: {linkDiag?.AsString}");
        }

        var slangReflection = linkedProgram.GetLayout(0, out _);
        var reflection = MapReflection(slangReflection);

        var vertexSpirV = GetEntryPointCode(linkedProgram, 0);
        byte[]? fragmentSpirV = null;
        if (fragmentEntryPoint != null)
        {
            fragmentSpirV = GetEntryPointCode(linkedProgram, 1);
        }

        return new CompiledShader
        {
            VertexSpirV = vertexSpirV,
            FragmentSpirV = fragmentSpirV,
            Reflection = reflection
        };
    }

    private static byte[] GetEntryPointCode(IComponentType linkedProgram, nint entryPointIndex)
    {
        if (!linkedProgram.GetEntryPointCode(entryPointIndex, 0, out var codeBlob, out var codeDiag).Succeeded)
        {
            throw new VkException($"Failed to get entry point {entryPointIndex} code: {codeDiag?.AsString}");
        }

        return codeBlob.Buffer.ToArray();
    }

    private static ShaderReflection MapReflection(SlangShaderReflection slangReflection)
    {
        var parameters = new List<ShaderParameterReflection>();
        var pushConstants = new List<PushConstantReflection>();

        for (uint i = 0; i < slangReflection.ParameterCount; i++)
        {
            var parameter = slangReflection.GetParameterByIndex(i);

            if (parameter.Category == SlangParameterCategory.PushConstantBuffer)
            {
                pushConstants.Add(MapPushConstant(parameter));
            }
            else
            {
                parameters.Add(MapParameter(parameter));
            }
        }

        var entryPointCount = (int)slangReflection.EntryPointCount;
        var entryPoints = new EntryPointReflection[entryPointCount];
        for (nuint i = 0; i < slangReflection.EntryPointCount; i++)
        {
            entryPoints[(int)i] = MapEntryPoint(slangReflection.GetEntryPointByIndex(i));
        }

        return new ShaderReflection
        {
            EntryPoints = entryPoints,
            Parameters = parameters.ToArray(),
            PushConstants = pushConstants.ToArray()
        };
    }

    private static PushConstantReflection MapPushConstant(VariableLayoutReflection parameter)
    {
        var name = parameter.Name;
        var typeLayout = parameter.TypeLayout;
        var elementLayout = typeLayout.ElementTypeLayout;
        var size = (uint)elementLayout.GetSize(SlangParameterCategory.Uniform);

        var fieldCount = (int)elementLayout.FieldCount;
        var fields = new PushConstantFieldReflection[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            var fieldLayout = elementLayout.GetFieldByIndex((uint)i);
            var fieldName = fieldLayout.Name;
            var fieldOffset = (uint)fieldLayout.GetOffset();
            fields[i] = new PushConstantFieldReflection(fieldName, fieldOffset);
        }

        return new PushConstantReflection(name, size, fields);
    }

    private static ShaderParameterReflection MapParameter(VariableLayoutReflection param)
    {
        var kind = MapParameterKind(param);
        var elementCount = GetElementCount(param);

        uint elementStride = 0;
        SsboFieldReflection[]? elementFields = null;

        if (kind == ShaderParameterKind.StorageBuffer)
        {
            (elementStride, elementFields) = GetSsboElementLayout(param);
        }

        return new ShaderParameterReflection(
            param.Name,
            param.BindingIndex,
            param.BindingSpace,
            kind,
            elementCount,
            elementStride,
            elementFields
        );
    }

    private static (uint stride, SsboFieldReflection[]? fields) GetSsboElementLayout(VariableLayoutReflection param)
    {
        var elementTypeLayout = param.TypeLayout.ElementTypeLayout;
        const SlangParameterCategory category = SlangParameterCategory.Uniform;

        var stride = (uint)elementTypeLayout.GetSize(category);

        var fieldCount = (int)elementTypeLayout.FieldCount;
        if (fieldCount == 0)
        {
            return (stride, null);
        }

        var fields = new SsboFieldReflection[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            var fieldLayout = elementTypeLayout.GetFieldByIndex((uint)i);
            var fieldName = fieldLayout.Name;
            var fieldOffset = (uint)fieldLayout.GetOffset(category);
            fields[i] = new SsboFieldReflection(fieldName, fieldOffset);
        }

        return (stride, fields);
    }

    private static uint GetElementCount(VariableLayoutReflection param)
    {
        var type = param.Type;
        if (!type.IsArray)
        {
            return 1;
        }

        var count = type.ElementCount;
        if (count == Slang.UnboundedSize || count == Slang.UnknownSize)
        {
            return 0;
        }

        return (uint)count;
    }

    private static ShaderParameterKind MapParameterKind(VariableLayoutReflection param)
    {
        var type = param.Type;

        if (type.IsArray)
        {
            type = type.ElementType;
        }

        return type.Kind switch
        {
            SlangTypeKind.ConstantBuffer => ShaderParameterKind.UniformBuffer,
            SlangTypeKind.SamplerState => ShaderParameterKind.Sampler,
            SlangTypeKind.Resource => MapResourceKind(type),
            SlangTypeKind.ShaderStorageBuffer => ShaderParameterKind.StorageBuffer,
            _ => throw new NotSupportedException($"Unsupported shader parameter type kind: {type.Kind}")
        };
    }

    private static ShaderParameterKind MapResourceKind(TypeReflection type)
    {
        var shape = type.ResourceShape & SlangResourceShape.BaseShapeMask;
        var access = type.ResourceAccess;

        if (shape == SlangResourceShape.StructuredBuffer || shape == SlangResourceShape.ByteAddressBuffer)
        {
            return ShaderParameterKind.StorageBuffer;
        }

        if (shape is
            SlangResourceShape.Texture1D or
            SlangResourceShape.Texture2D or
            SlangResourceShape.Texture3D or
            SlangResourceShape.TextureCube or
            SlangResourceShape.TextureBuffer)
        {
            return (access & SlangResourceAccess.Write) != 0
                ? ShaderParameterKind.StorageImage
                : ShaderParameterKind.SampledImage;
        }

        throw new NotSupportedException($"Unsupported resource shape: {shape}");
    }

    private static EntryPointReflection MapEntryPoint(SlangEntryPointReflection slangEntryPoint)
    {
        var stage = MapStage(slangEntryPoint.Stage);
        var vertexInputs = Array.Empty<VertexAttributeReflection>();

        if (stage == ShaderStage.Vertex)
        {
            vertexInputs = MapVertexInputs(slangEntryPoint);
        }

        return new EntryPointReflection(slangEntryPoint.Name, stage, vertexInputs);
    }

    private static ShaderStage MapStage(SlangStage stage) => stage switch
    {
        SlangStage.Vertex => ShaderStage.Vertex,
        SlangStage.Fragment => ShaderStage.Fragment,
        SlangStage.Compute => ShaderStage.Compute,
        _ => throw new NotSupportedException($"Unsupported shader stage: {stage}")
    };

    private static VertexAttributeReflection[] MapVertexInputs(SlangEntryPointReflection slangEntryPoint)
    {
        var paramCount = slangEntryPoint.ParameterCount;
        if (paramCount == 0)
        {
            return [];
        }

        var param = slangEntryPoint.GetParameterByIndex(0);
        var type = param.Type;

        if (type.Kind != SlangTypeKind.Struct)
        {
            return [];
        }

        var fieldCount = (int)type.FieldCount;
        var attributes = new VertexAttributeReflection[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            var field = type.GetFieldByIndex((uint)i);
            var fieldType = field.Type;
            var format = MapVertexFormat(fieldType);
            var location = (uint)i;
            var offset = (uint)0;

            attributes[i] = new VertexAttributeReflection(field.Name, location, format, offset);
        }

        return attributes;
    }

    private static VertexAttributeFormat MapVertexFormat(TypeReflection type)
    {
        if (type.Kind == SlangTypeKind.Vector)
        {
            var scalar = type.ScalarType;
            var columns = (int)type.ColumnCount;

            return (scalar, cols: columns) switch
            {
                (SlangScalarType.Float32, 1) => VertexAttributeFormat.Float,
                (SlangScalarType.Float32, 2) => VertexAttributeFormat.Float2,
                (SlangScalarType.Float32, 3) => VertexAttributeFormat.Float3,
                (SlangScalarType.Float32, 4) => VertexAttributeFormat.Float4,
                (SlangScalarType.Int32, 1) => VertexAttributeFormat.Int,
                (SlangScalarType.Int32, 2) => VertexAttributeFormat.Int2,
                (SlangScalarType.Int32, 3) => VertexAttributeFormat.Int3,
                (SlangScalarType.Int32, 4) => VertexAttributeFormat.Int4,
                (SlangScalarType.UInt32, 1) => VertexAttributeFormat.UInt,
                (SlangScalarType.UInt32, 2) => VertexAttributeFormat.UInt2,
                (SlangScalarType.UInt32, 3) => VertexAttributeFormat.UInt3,
                (SlangScalarType.UInt32, 4) => VertexAttributeFormat.UInt4,
                _ => throw new NotSupportedException($"Unsupported vertex format: scalar={scalar}, cols={columns}")
            };
        }

        if (type.Kind == SlangTypeKind.Scalar)
        {
            return type.ScalarType switch
            {
                SlangScalarType.Float32 => VertexAttributeFormat.Float,
                SlangScalarType.Int32 => VertexAttributeFormat.Int,
                SlangScalarType.UInt32 => VertexAttributeFormat.UInt,
                _ => throw new NotSupportedException($"Unsupported scalar vertex format: {type.ScalarType}")
            };
        }

        throw new NotSupportedException($"Unsupported vertex input type kind: {type.Kind}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            
        }

        // Empty for now
    }
}
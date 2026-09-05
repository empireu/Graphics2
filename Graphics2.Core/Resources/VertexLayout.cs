using System.Reflection;
using System.Runtime.InteropServices;

namespace Graphics2.Core.Resources;

public enum IndexType
{
    UInt16,
    UInt32
}

public readonly struct VertexLayoutAttribute(string name, VertexAttributeFormat format, uint offset)
{
    public readonly string Name = name;
    public readonly VertexAttributeFormat Format = format;
    public readonly uint Offset = offset;
}

public sealed class VertexLayout
{
    private static readonly Dictionary<Type, VertexAttributeFormat> RegisteredTypes = new()
    {
        { typeof(float), VertexAttributeFormat.Float },
        { typeof(int), VertexAttributeFormat.Int },
        { typeof(uint), VertexAttributeFormat.UInt },
        { typeof(Silk.NET.Maths.Vector2D<float>), VertexAttributeFormat.Float2 },
        { typeof(Silk.NET.Maths.Vector3D<float>), VertexAttributeFormat.Float3 },
        { typeof(Silk.NET.Maths.Vector4D<float>), VertexAttributeFormat.Float4 },
        { typeof(System.Numerics.Vector2), VertexAttributeFormat.Float2 },
        { typeof(System.Numerics.Vector3), VertexAttributeFormat.Float3 },
        { typeof(System.Numerics.Vector4), VertexAttributeFormat.Float4 }
    };

    public required uint Stride { get; init; }
    public required VertexLayoutAttribute[] Attributes { get; init; }

    public static void RegisterType(Type type, VertexAttributeFormat format)
    {
        RegisteredTypes[type] = format;
    }

    public static VertexLayout FromStruct<T>() where T : struct
    {
        var type = typeof(T);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var attributes = new VertexLayoutAttribute[fields.Length];

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var format = TypeToFormat(field.FieldType);
            var offset = (uint)Marshal.OffsetOf<T>(field.Name);
            attributes[i] = new VertexLayoutAttribute(field.Name, format, offset);
        }

        return new VertexLayout
        {
            Stride = (uint)Marshal.SizeOf<T>(),
            Attributes = attributes
        };
    }

    private static VertexAttributeFormat TypeToFormat(Type type)
    {
        if (RegisteredTypes.TryGetValue(type, out var format))
        {
            return format;
        }

        throw new NotSupportedException($"Unsupported vertex field type: {type}. Register it.");
    }
}

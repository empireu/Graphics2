using System.Reflection;
using System.Runtime.InteropServices;

namespace Graphics2.Core.Resources;

public readonly struct SsboField(string name, uint offset)
{
    public readonly string Name = name;
    public readonly uint Offset = offset;
}

public sealed class SsboLayout
{
    public required uint Stride { get; init; }
    public required SsboField[] Fields { get; init; }

    public static SsboLayout FromStruct<T>() where T : unmanaged
    {
        var type = typeof(T);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var layoutFields = new SsboField[fields.Length];

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var offset = (uint)Marshal.OffsetOf<T>(field.Name);
            layoutFields[i] = new SsboField(field.Name, offset);
        }

        return new SsboLayout
        {
            Stride = (uint)Marshal.SizeOf<T>(),
            Fields = layoutFields
        };
    }
}

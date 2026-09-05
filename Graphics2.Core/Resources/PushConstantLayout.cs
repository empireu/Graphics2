using System.Reflection;
using System.Runtime.InteropServices;

namespace Graphics2.Core.Resources;

public readonly struct PushConstantField(string name, uint offset)
{
    public readonly string Name = name;
    public readonly uint Offset = offset;
}

public sealed class PushConstantLayout
{
    public required uint Size { get; init; }
    public required PushConstantField[] Fields { get; init; }

    public static PushConstantLayout FromStruct<T>() where T : unmanaged
    {
        var type = typeof(T);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var layoutFields = new PushConstantField[fields.Length];

        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var offset = (uint)Marshal.OffsetOf<T>(field.Name);
            layoutFields[i] = new PushConstantField(field.Name, offset);
        }

        return new PushConstantLayout
        {
            Size = (uint)Marshal.SizeOf<T>(),
            Fields = layoutFields
        };
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using Graphics2.Core.Resources;

namespace Graphics2.Core;

public static unsafe class Helpers
{
    public static Stream GetResourceStream(Assembly assembly, string path)
    {
        var stream = assembly.GetManifestResourceStream(path);

        if (stream == null)
        {
            throw new FileNotFoundException($"Could not get embedded resource \"{path}\"");
        }

        return stream;
    }

    public static Stream GetResourceStream(string path)
    {
        return GetResourceStream(Assembly.GetExecutingAssembly(), path);
    }

    public static byte[] GetResourceBytes(Assembly assembly, string path)
    {
        using var stream = GetResourceStream(assembly, path);

        Span<byte> buffer = stackalloc byte[1024];
        var result = new List<byte>();

        while (true)
        {
            var read = stream.Read(buffer);

            if (read == 0)
            {
                break;
            }

            result.AddRange(buffer[..read]);
        }

        return result.ToArray();
    }

    public static byte[] GetResourceBytes(string path)
    {
        return GetResourceBytes(Assembly.GetExecutingAssembly(), path);
    }

    public static string GetResourceText(Assembly assembly, string path)
    {
        using var stream = GetResourceStream(assembly, path);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    public static string GetResourceText(string path)
    {
        return GetResourceText(Assembly.GetExecutingAssembly(), path);
    }

    public static System.Numerics.Matrix4x4 RefCast(ref this Silk.NET.Maths.Matrix4X4<float> source)
    {
        return Unsafe.As<Silk.NET.Maths.Matrix4X4<float>, System.Numerics.Matrix4x4>(ref source);
    }
    
    public static System.Numerics.Matrix4x4 Cast(this Silk.NET.Maths.Matrix4X4<float> source)
    {
        return Unsafe.As<Silk.NET.Maths.Matrix4X4<float>, System.Numerics.Matrix4x4>(ref source);
    }

    public static int Stride(this IndexType indexType) => indexType switch
    {
        IndexType.UInt16 => 2,
        IndexType.UInt32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(indexType), indexType, null)
    };
}
using System.Reflection;

namespace Demo;

internal static class Assets
{
    public static Stream GetResourceStream(string path)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var stream = assembly.GetManifestResourceStream(path);

        if (stream == null)
        {
            throw new FileNotFoundException($"Could not get embedded resource \"{path}\"");
        }

        return stream;
    }

    public static string GetResourceText(string path)
    {
        using var stream = GetResourceStream(path);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
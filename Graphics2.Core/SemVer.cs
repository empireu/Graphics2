namespace Graphics2.Core;

public readonly struct SemVer(int major, int minor, int patch)
{
    public static readonly SemVer Null = new(0, 0, 0);
    
    public int Major { get; } = major;
    public int Minor { get; } = minor;
    public int Patch { get; } = patch;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
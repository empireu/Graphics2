using System.Numerics;
using System.Runtime.InteropServices;

namespace Graphics2.Renderer.Quads;

[StructLayout(LayoutKind.Sequential)]
public struct SolidQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color;
    public Vector2 Size;
    private Vector2 _pad;
}

[StructLayout(LayoutKind.Sequential)]
public struct TexturedQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color;
    public Vector2 Size;
    public Vector2 UvMin;
    public Vector2 UvMax;
    private Vector2 _pad;
}

[StructLayout(LayoutKind.Sequential)]
public struct CircleQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public struct GradientQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color0;
    public Vector4 Color1;
    public Vector4 Color2;
    public Vector4 Color3;
    public Vector2 Size;
    private Vector2 _pad;
}

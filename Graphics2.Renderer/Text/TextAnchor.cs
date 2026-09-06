using System.Numerics;

namespace Graphics2.Renderer.Text;

public enum TextAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center
}

public static class TextAnchorExtensions
{
    public static Vector2 Offset(this TextAnchor anchor, Vector2 size, float ascent)
    {
        var top = -ascent;
        var bottom = top + size.Y;

        return anchor switch
        {
            TextAnchor.TopLeft => new Vector2(0.0f, -top),
            TextAnchor.TopRight => new Vector2(-size.X, -top),
            TextAnchor.BottomLeft => new Vector2(0.0f, -bottom),
            TextAnchor.BottomRight => new Vector2(-size.X, -bottom),
            TextAnchor.Center => new Vector2(-size.X * 0.5f, -(top + bottom) * 0.5f),
            _ => Vector2.Zero
        };
    }
}

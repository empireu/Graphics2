using System.Numerics;

namespace Graphics2.Renderer.Quads;

public enum QuadAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center
}

public static class QuadAnchorExtensions
{
    public static Vector2 Offset(this QuadAnchor anchor, Vector2 size)
    {
        return anchor switch
        {
            QuadAnchor.TopLeft => Vector2.Zero,
            QuadAnchor.TopRight => new Vector2(-size.X, 0.0f),
            QuadAnchor.BottomLeft => new Vector2(0.0f, -size.Y),
            QuadAnchor.BottomRight => new Vector2(-size.X, -size.Y),
            QuadAnchor.Center => new Vector2(-size.X * 0.5f, -size.Y * 0.5f),
            _ => Vector2.Zero
        };
    }
}

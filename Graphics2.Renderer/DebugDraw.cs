using System.Numerics;
using Graphics2.Renderer.Lines;
// ReSharper disable UseCollectionExpression

namespace Graphics2.Renderer;

public static class DebugDraw
{
    public static void DrawBoundingBox(this LineRenderer3D renderer, Vector3 min, Vector3 max, Vector4 color, float radius = 0.05f)
    {
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z)
        };

        ReadOnlySpan<int> edges = stackalloc int[24]
        {
            0, 1, 1, 3, 
            3, 2, 2, 0,
            4, 5, 5, 7, 
            7, 6, 6, 4, 
            0, 4, 1, 5,
            2, 6, 3, 7
        };

        for (var index = 0; index < edges.Length; index += 2)
        {
            renderer.DrawLine3D(corners[edges[index]], corners[edges[index + 1]], radius, color);
        }
    }
}
using System.Numerics;

namespace Graphics2.Mathematics;

/// <summary>
///     The volume a view-projection matrix keeps, as six planes facing inwards: the points whose clip coordinates have x and y within [-w, w] and z within [0, w], which is Vulkan's clip volume.
///     The matrix multiplies row vectors, as <see cref="Matrix4x4"/> does, and may be a perspective or an orthographic projection.
/// </summary>
public readonly struct Frustum
{
    private readonly Plane _left;
    private readonly Plane _right;
    private readonly Plane _bottom;
    private readonly Plane _top;
    private readonly Plane _near;
    private readonly Plane _far;

    public Frustum(Matrix4x4 viewProjection)
    {
        var x = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        var y = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        var z = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        var w = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        _left = new Plane(w + x);
        _right = new Plane(w - x);
        _bottom = new Plane(w + y);
        _top = new Plane(w - y);
        _near = new Plane(z);
        _far = new Plane(w - z);
    }

    /// <summary>
    ///     Whether the box from <paramref name="min"/> to <paramref name="max"/> may reach into the volume. It is false only for a box wholly outside one of the planes, so a box near a corner of the volume can pass without reaching into it.
    /// </summary>
    public bool Intersects(Vector3 min, Vector3 max)
    {
        var center = 0.5f * (min + max);
        var extent = 0.5f * (max - min);

        return !IsOutside(_left, center, extent) &&
               !IsOutside(_right, center, extent) &&
               !IsOutside(_bottom, center, extent) &&
               !IsOutside(_top, center, extent) &&
               !IsOutside(_near, center, extent) &&
               !IsOutside(_far, center, extent);
    }

    private static bool IsOutside(in Plane plane, Vector3 center, Vector3 extent)
    {
        return Plane.DotCoordinate(plane, center) + Vector3.Dot(Vector3.Abs(plane.Normal), extent) < 0.0f;
    }
}

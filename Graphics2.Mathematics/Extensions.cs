namespace Graphics2.Mathematics;

public static class Extensions
{
    public static T Also<T>(this T t, Action<T> action)
    {
        action(t);
        return t;
    }

    public static T2 Map<T1, T2>(this T1 t, Func<T1, T2> m)
    {
        return m(t);
    }
    
    public static Base6Direction3D Opposite(this Base6Direction3D dir) => dir switch
    {
        Base6Direction3D.L => Base6Direction3D.R,
        Base6Direction3D.R => Base6Direction3D.L,
        Base6Direction3D.U => Base6Direction3D.D,
        Base6Direction3D.D => Base6Direction3D.U,
        Base6Direction3D.F => Base6Direction3D.B,
        Base6Direction3D.B => Base6Direction3D.F,
        _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, null)
    };

    public static Vector3i Step(this Base6Direction3D dir) => dir switch
    {
        Base6Direction3D.L => new Vector3i(-1, 0, 0),
        Base6Direction3D.R => new Vector3i(1, 0, 0),
        Base6Direction3D.U => new Vector3i(0, 1, 0),
        Base6Direction3D.D => new Vector3i(0, -1, 0),
        Base6Direction3D.F => new Vector3i(0, 0, -1),
        Base6Direction3D.B => new Vector3i(0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(dir), dir, null)
    };

    public static Base6Direction3DMask Mask(this Base6Direction3D dir) => (Base6Direction3DMask)(1 << (int)dir);

    public static bool ApproxEq(this float f, float other, float threshold = 1e-7f)
    {
        return Math.Abs(f - other) < threshold;
    }

    public static bool ApproxEq(this double d, double other, double threshold = 1e-7)
    {
        return Math.Abs(d - other) < threshold;
    }
}
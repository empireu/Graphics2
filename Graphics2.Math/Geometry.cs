using System.Numerics;
using System.Text.Json.Serialization;

// ReSharper disable InconsistentNaming

namespace Graphics2.Math;

public enum Base4Direction2D : byte
{
    U,
    L,
    D,
    R
}

public enum Base8Direction2D : byte
{
    U,
    LU,
    L,
    LD,
    D,
    RD,
    R,
    RU
}

public enum Base6Direction3D : byte
{
    L = 0,
    R = 1,
    U = 2,
    D = 3,
    F = 4,
    B = 5
}

[Flags]
public enum Base6Direction3DMask : byte
{
    None = 0,
    L = 1 << 0,
    R = 1 << 1,
    U = 1 << 2,
    D = 1 << 3,
    F = 1 << 4,
    B = 1 << 5,
}

public struct Vector2d
{
    public static readonly Vector2d Zero = new(0, 0);
    public static readonly Vector2d One = new(1, 1);
    public static readonly Vector2d UnitX = new(1, 0);
    public static readonly Vector2d UnitY = new(0, 1);

    public double X;
    public double Y;

    public Vector2d(double x, double y)
    {
        X = x;
        Y = y;
    }

    public Vector2d(double value) : this(value, value)
    {
    }

    // We may consider using functions here. Normalized() is already using it.
    [JsonIgnore]
    public double NormSqr => X * X + Y * Y;
    [JsonIgnore]
    public double Norm => System.Math.Sqrt(NormSqr);
    public Vector2d Normalized() => this / Norm;

    public override bool Equals(object? obj)
    {
        if (obj is not Vector2d other)
        {
            return false;
        }

        return Equals(other);
    }

    public bool Equals(Vector2d other) => X.Equals(other.X) && Y.Equals(other.Y);

    public bool ApproxEq(Vector2d other, double eps = 1e-7) => X.ApproxEq(other.X, eps) && Y.ApproxEq(other.Y, eps);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() => $"X={X}, Y={Y}";

    public static bool operator ==(Vector2d a, Vector2d b) => a.Equals(b);
    public static bool operator !=(Vector2d a, Vector2d b) => !a.Equals(b);
    public static Vector2d operator +(Vector2d v) => new(+v.X, +v.Y);
    public static Vector2d operator -(Vector2d v) => new(-v.X, -v.Y);
    public static Vector2d operator +(Vector2d a, Vector2d b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2d operator -(Vector2d a, Vector2d b) => new(a.X - b.X, a.Y - b.Y);
    public static Vector2d operator *(Vector2d a, Vector2d b) => new(a.X * b.X, a.Y * b.Y);
    public static Vector2d operator /(Vector2d a, Vector2d b) => new(a.X / b.X, a.Y / b.Y);
    public static Vector2d operator *(Vector2d v, double scalar) => new(v.X * scalar, v.Y * scalar);
    public static Vector2d operator /(Vector2d v, double scalar) => new(v.X / scalar, v.Y / scalar);

    public static implicit operator Vector2d(Vector2 v) => new(v.X, v.Y);
    public static implicit operator Vector2(Vector2d v) => new((float)v.X, (float)v.Y);
}

public struct Vector3i : IComparable<Vector3i>
{
    public static readonly Vector3i Zero = new(0, 0, 0);
    public static readonly Vector3i UnitX = new(1, 0, 0);
    public static readonly Vector3i UnitY = new(0, 1, 0);
    public static readonly Vector3i UnitZ = new(0, 0, 1);
    public static readonly Vector3i One = new(1, 1, 1);

    public int X;
    public int Y;
    public int Z;

    public int NormSqr => X * X + Y * Y + Z * Z;
    public double Norm => System.Math.Sqrt(NormSqr);
    public float NormF => MathF.Sqrt(NormSqr);

    public Vector3i(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Vector3i(int value)
    {
        X = value;
        Y = value;
        Z = value;
    }

    public override string ToString() => $"X={X}, Y={Y}, Z={Z}";

    public override bool Equals(object? obj) => obj is Vector3i v && this.Equals(v);

    public bool Equals(Vector3i other) => X == other.X && Y == other.Y && Z == other.Z;

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public int CompareTo(Vector3i other) => NormSqr.CompareTo(other.NormSqr);

    public static Vector3i operator +(Vector3i a) => a;
    public static Vector3i operator -(Vector3i a) => new(-a.X, -a.Y, -a.Z);
    public static Vector3i operator ~(Vector3i a) => new(~a.X, ~a.Y, ~a.Z);
    public static Vector3i operator +(Vector3i a, Vector3i b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3i operator +(Vector3i a, int b) => new(a.X + b, a.Y + b, a.Z + b);
    public static Vector3i operator -(Vector3i a, Vector3i b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3i operator -(Vector3i a, int b) => new(a.X - b, a.Y - b, a.Z - b);
    public static Vector3i operator *(Vector3i a, Vector3i b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vector3i operator *(Vector3i a, int scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);
    public static Vector3i operator /(Vector3i a, Vector3i b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
    public static Vector3i operator /(Vector3i a, int scalar) => new(a.X / scalar, a.Y / scalar, a.Z / scalar);
    public static Vector3i operator <<(Vector3i a, int shl) => new(a.X << shl, a.Y << shl, a.Z << shl);
    public static Vector3i operator >>(Vector3i a, int shr) => new(a.X >> shr, a.Y >> shr, a.Z >> shr);
    public static Vector3i operator <<(Vector3i a, Vector3i b) => new(a.X << b.X, a.Y << b.Y, a.Z << b.Z);
    public static Vector3i operator >>(Vector3i a, Vector3i b) => new(a.X >> b.X, a.Y >> b.Y, a.Z >> b.Z);
    public static Vector3i operator &(Vector3i a, int b) => new(a.X & b, a.Y & b, a.Z & b);
    public static Vector3i operator &(Vector3i a, Vector3i b) => new(a.X & b.X, a.Y & b.Y, a.Z & b.Z);
    public static Vector3i operator |(Vector3i a, int b) => new(a.X | b, a.Y | b, a.Z | b);
    public static Vector3i operator |(Vector3i a, Vector3i b) => new(a.X | b.X, a.Y | b.Y, a.Z | b.Z);
    public static Vector3i operator ^(Vector3i a, int b) => new(a.X ^ b, a.Y ^ b, a.Z ^ b);
    public static Vector3i operator ^(Vector3i a, Vector3i b) => new(a.X ^ b.X, a.Y ^ b.Y, a.Z ^ b.Z);
    public static Vector3i operator %(Vector3i a, int b) => new(a.X % b, a.Y % b, a.Z % b);
    public static Vector3i operator %(Vector3i a, Vector3i b) => new(a.X % b.X, a.Y % b.Y, a.Z % b.Z);
    public static bool operator <(Vector3i a, int b) => a.X < b && a.Y < b && a.Z < b;
    public static bool operator >(Vector3i a, int b) => a.X > b && a.Y > b && a.Z > b;
    public static bool operator <=(Vector3i a, int b) => a.X <= b && a.Y <= b && a.Z <= b;
    public static bool operator >=(Vector3i a, int b) => a.X >= b && a.Y >= b && a.Z >= b;

    public static bool operator ==(Vector3i a, Vector3i b) => a.Equals(b);
    public static bool operator !=(Vector3i a, Vector3i b) => !a.Equals(b);

    public static implicit operator Vector3(Vector3i v) => new(v.X, v.Y, v.Z);
    public static implicit operator Vector3d(Vector3i v) => new(v.X, v.Y, v.Z);
    public static explicit operator Vector3i(Vector3 v) => new((int)v.X, (int)v.Y, (int)v.Z);

    public static int DistanceSqr(Vector3i a, Vector3i b) => (a - b).NormSqr;
    public static double Distance(Vector3i a, Vector3i b) => (a - b).Norm;
    public static float DistanceF(Vector3i a, Vector3i b) => (a - b).NormF;
    public static int Manhattan(Vector3i a, Vector3i b) => System.Math.Abs(a.X - b.X) + System.Math.Abs(a.Y - b.Y) + System.Math.Abs(a.Z - b.Z);

    public static Vector3i Clamp(Vector3i v, Vector3i min, Vector3i max) => new(
        System.Math.Clamp(v.X, min.X, max.X),
        System.Math.Clamp(v.Y, min.Y, max.Y),
        System.Math.Clamp(v.Z, min.Z, max.Z)
    );

    public static Vector3i ClampExclusive(Vector3i v, Vector3i min, Vector3i maxExclusive) => new(
        System.Math.Clamp(v.X, min.X, maxExclusive.X - 1),
        System.Math.Clamp(v.Y, min.Y, maxExclusive.Y - 1),
        System.Math.Clamp(v.Z, min.Z, maxExclusive.Z - 1)
    );

    public static Vector3i Clamp(Vector3i v, int min, int max) => new(
        System.Math.Clamp(v.X, min, max),
        System.Math.Clamp(v.Y, min, max),
        System.Math.Clamp(v.Z, min, max)
    );

    public static Vector3i Min(Vector3i a, Vector3i b) => new(
        System.Math.Min(a.X, b.X),
        System.Math.Min(a.Y, b.Y),
        System.Math.Min(a.Z, b.Z)
    );

    public static Vector3i Max(Vector3i a, Vector3i b) => new(
        System.Math.Max(a.X, b.X),
        System.Math.Max(a.Y, b.Y),
        System.Math.Max(a.Z, b.Z)
    );
}

public struct Vector3d
{
    public static readonly Vector3d Zero = new(0, 0, 0);
    public static readonly Vector3d One = new(1, 1, 1);
    public static readonly Vector3d UnitX = new(1, 0, 0);
    public static readonly Vector3d UnitY = new(0, 1, 0);
    public static readonly Vector3d UnitZ = new(0, 0, 1);

    public double X;
    public double Y;
    public double Z;

    public Vector3d(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public Vector3d(double value)
    {
        X = value;
        Y = value;
        Z = value;
    }

    public double NormSqr => Dot(this, this);
    public double Norm => System.Math.Sqrt(NormSqr);
    public Vector3d Normalized() => this / Norm;

    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    
    public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double DistanceSqr(Vector3d a, Vector3d b) => (a - b).NormSqr;
    public static double Distance(Vector3d a, Vector3d b) => (a - b).Norm;
    public static Vector3d Lerp(Vector3d a, Vector3d b, double t) => new(MathX.Lerp(a.X, b.X, t), MathX.Lerp(a.Y, b.Y, t), MathX.Lerp(a.Z, b.Z, t));

    public static Vector3d operator +(Vector3d v) => v;
    public static Vector3d operator -(Vector3d v) => new(-v.X, -v.Y, -v.Z);
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, Vector3d b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static Vector3d operator /(Vector3d a, Vector3d b) => new(a.X / b.X, a.Y / b.Y, a.Z / b.Z);
    public static Vector3d operator *(Vector3d a, double scalar) => new(a.X * scalar, a.Y * scalar, a.Z * scalar);
    public static Vector3d operator /(Vector3d a, double scalar) => new(a.X / scalar, a.Y / scalar, a.Z / scalar);

    public static implicit operator Vector3d(Vector3 v) => new(v.X, v.Y, v.Z);
    public static implicit operator Vector3(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    public static Vector3 Max(Vector3 a, Vector3 b) => new(
        a.X > b.X ? a.X : b.X,
        a.Y > b.Y ? a.Y : b.Y,
        a.Z > b.Z ? a.Z : b.Z
    );

    public static Vector3d Min(Vector3d a, Vector3d b) => new(
        a.X < b.X ? a.X : b.X,
        a.Y < b.Y ? a.Y : b.Y,
        a.Z < b.Z ? a.Z : b.Z
    );

    public static Vector3d Clamp(Vector3d value, Vector3d min, Vector3d max) =>
        Min(Max(value, min), max);

    public static double Angle(Vector3d a, Vector3d b) =>
        System.Math.Acos(System.Math.Clamp(Vector3d.Dot(a, b) / (a.Norm * b.Norm), -1, +1));
}

public struct BoundingBox3i
{
    public Vector3i Min;
    public Vector3i Max;

    public BoundingBox3i(Vector3i min, Vector3i max)
    {
        Min = min;
        Max = max;
    }

    public BoundingBox3i(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
    {
        Min = new Vector3i(minX, minY, minZ);
        Max = new Vector3i(maxX, maxY, maxZ);
    }

    public bool IsValid => Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

    public int Volume => (Max.X - Min.X) * (Max.Y - Min.Y) * (Max.Z - Min.Z);

    public Vector3d Center => new((Min.X + Max.X) / 2d, (Min.Y + Max.Y) / 2d, (Min.Z + Max.Z) / 2d);

    public bool Contains(Vector3i point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    public bool Contains(int x, int y, int z) =>
        x >= Min.X && x <= Max.X &&
        y >= Min.Y && y <= Max.Y &&
        z >= Min.Z && z <= Max.Z;

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    public bool ContainsExclusive(Vector3i point) =>
        point.X >= Min.X && point.X < Max.X &&
        point.Y >= Min.Y && point.Y < Max.Y &&
        point.Z >= Min.Z && point.Z < Max.Z;

    public bool ContainsExclusive(int x, int y, int z) =>
        x >= Min.X && x < Max.X &&
        y >= Min.Y && y < Max.Y &&
        z >= Min.Z && z < Max.Z;

    public bool Intersects(BoundingBox3i box) => 
        Max.X >= box.Min.X && Min.X <= box.Max.X && 
        Max.Y >= box.Min.Y && Min.Y <= box.Max.Y && 
        Max.Z >= box.Min.Z && Min.Z <= box.Max.Z;

    public int DistanceToSqr(Vector3i point) => Contains(point) ? 0 : Vector3i.DistanceSqr(point, Vector3i.Clamp(point, Min, Max));
    public float DistanceToSqr(Vector3 point) => Contains(point) ? 0 : Vector3.DistanceSquared(point, Vector3.Clamp(point, Min, Max));

    public double DistanceTo(Vector3i point) => System.Math.Sqrt(DistanceToSqr(point));
    public float DistanceFTo(Vector3i point) => MathF.Sqrt(DistanceToSqr(point));

    public static BoundingBox3i Intersect(BoundingBox3i a, BoundingBox3i b) => new(
        System.Math.Max(a.Min.X, b.Min.X),
        System.Math.Max(a.Min.Y, b.Min.Y),
        System.Math.Max(a.Min.Z, b.Min.Z),
        System.Math.Min(a.Max.X, b.Max.X),
        System.Math.Min(a.Max.Y, b.Max.Y),
        System.Math.Min(a.Max.Z, b.Max.Z)    
    );
}

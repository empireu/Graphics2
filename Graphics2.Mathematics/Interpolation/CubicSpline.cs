// ReSharper disable ForCanBeConvertedToForeach

namespace Graphics2.Mathematics.Interpolation;

/// <summary>
///     A cubic Hermite spline over strictly increasing control positions, with Catmull-Rom tangents.
///     The input is clamped to the control range, so the curve is constant outside it.
/// </summary>
public readonly struct CubicSpline : IEquatable<CubicSpline>
{
    private readonly float[] _positions;
    private readonly float[] _values;
    private readonly float[] _tangents;

    public CubicSpline(float[] positions, float[] values)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(values);

        if (positions.Length != values.Length)
        {
            throw new ArgumentException("A spline has one value per control position.", nameof(values));
        }

        if (positions.Length < 2)
        {
            throw new ArgumentException("A spline needs at least two control points.", nameof(positions));
        }

        for (var index = 1; index < positions.Length; index++)
        {
            if (positions[index] <= positions[index - 1])
            {
                throw new ArgumentException("The control positions have to increase strictly.", nameof(positions));
            }
        }

        _positions = (float[])positions.Clone();
        _values = (float[])values.Clone();
        _tangents = ComputeTangents(_positions, _values);
    }

    public float Evaluate(float input)
    {
        var last = _positions.Length - 1;

        if (input <= _positions[0])
        {
            return _values[0];
        }

        if (input >= _positions[last])
        {
            return _values[last];
        }

        var index = 0;
    
        // P.S. binary search possible.
        while (index < last - 1 && input >= _positions[index + 1])
        {
            index++;
        }

        var from = _positions[index];
        var to = _positions[index + 1];
        var span = to - from;
        var t = (input - from) / span;
        var t2 = t * t;
        var t3 = t2 * t;

        var h00 = 2.0f * t3 - 3.0f * t2 + 1.0f;
        var h10 = t3 - 2.0f * t2 + t;
        var h01 = -2.0f * t3 + 3.0f * t2;
        var h11 = t3 - t2;

        return h00 * _values[index] 
               + h10 * span * _tangents[index]
               + h01 * _values[index + 1]
               + h11 * span * _tangents[index + 1];
    }

    private static float[] ComputeTangents(float[] positions, float[] values)
    {
        var last = positions.Length - 1;
        var tangents = new float[positions.Length];

        tangents[0] = (values[1] - values[0]) / (positions[1] - positions[0]);
        tangents[last] = (values[last] - values[last - 1]) / (positions[last] - positions[last - 1]);

        for (var index = 1; index < last; index++)
        {
            tangents[index] = (values[index + 1] - values[index - 1]) / (positions[index + 1] - positions[index - 1]);
        }

        return tangents;
    }

    public bool Equals(CubicSpline other)
    {
        return _positions.AsSpan().SequenceEqual(other._positions) && _values.AsSpan().SequenceEqual(other._values) && _tangents.AsSpan().SequenceEqual(other._tangents);
    }

    public override bool Equals(object? obj)
    {
        return obj is CubicSpline other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();

        for (var index = 0; index < _positions.Length; index++)
        {
            hash.Add(_positions[index]);
        }

        for (var index = 0; index < _values.Length; index++)
        {
            hash.Add(_values[index]);
        }

        for (var index = 0; index < _tangents.Length; index++)
        {
            hash.Add(_tangents[index]);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(CubicSpline left, CubicSpline right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(CubicSpline left, CubicSpline right)
    {
        return !left.Equals(right);
    }
}

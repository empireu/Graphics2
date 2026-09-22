namespace WinterGame.World.Generation;

/// <summary>
///     How one unit of an aligned province pair is present on each side.
/// </summary>
internal enum StrataColumnKind : byte
{
    /// <summary>
    ///     Both provinces hold the unit, so its thickness interpolates and it never vanishes.
    /// </summary>
    Shared,
    
    /// <summary>
    ///     Only the left province holds the unit, so it wedges out as the transition approaches its middle.
    /// </summary>
    LeftOnly,

    /// <summary>
    ///     Only the right province holds the unit, so it appears as the transition passes its middle.
    /// </summary>
    RightOnly
}

/// <summary>
///     Two province tables aligned by id, built once and evaluated per sample.
///     Shared units interpolate their thickness, and a unit only one side holds scales by a window that reaches zero at the middle of the transition.
///     Every id change therefore happens inside a slot of zero thickness, which is what turns the province boundary from a vertical contact into a pinch-out.
/// </summary>
internal sealed class StrataTransition
{
    private readonly StrataColumnKind[] _kinds;
    private readonly byte[] _ids;
    private readonly float[] _leftThicknesses;
    private readonly float[] _rightThicknesses;

    private readonly float _basementTopY;
    private readonly byte _belowLeft;
    private readonly byte _belowRight;
    private readonly byte _topLeft;
    private readonly byte _topRight;

    private StrataTransition(
        StrataColumnKind[] kinds,
        byte[] ids,
        float[] leftThicknesses,
        float[] rightThicknesses,
        StrataTable left,
        StrataTable right)
    {
        _kinds = kinds;
        _ids = ids;
        _leftThicknesses = leftThicknesses;
        _rightThicknesses = rightThicknesses;
        _basementTopY = left.BasementTopY;
        _belowLeft = left.BelowId;
        _belowRight = right.BelowId;
        _topLeft = left.BandId(left.BandCount - 1);
        _topRight = right.BandId(right.BandCount - 1);
    }

    /// <summary>
    ///     Aligns two tables by their id sequence with a longest common subsequence, so units of the same rock correspond and everything else is a one-sided unit that pinches out.
    /// </summary>
    public static StrataTransition Align(StrataTable left, StrataTable right)
    {
        var leftCount = left.BandCount;
        var rightCount = right.BandCount;

        var lcs = new int[leftCount + 1, rightCount + 1];

        for (var i = 1; i <= leftCount; i++)
        {
            for (var j = 1; j <= rightCount; j++)
            {
                lcs[i, j] = left.BandId(i - 1) == right.BandId(j - 1)
                    ? lcs[i - 1, j - 1] + 1
                    : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            }
        }

        var kinds = new List<StrataColumnKind>(leftCount + rightCount);
        var ids = new List<byte>(leftCount + rightCount);
        var leftThicknesses = new List<float>(leftCount + rightCount);
        var rightThicknesses = new List<float>(leftCount + rightCount);

        var leftIndex = leftCount;
        var rightIndex = rightCount;

        while (leftIndex > 0 || rightIndex > 0)
        {
            if (leftIndex > 0
                && rightIndex > 0
                && left.BandId(leftIndex - 1) == right.BandId(rightIndex - 1)
                && lcs[leftIndex, rightIndex] == lcs[leftIndex - 1, rightIndex - 1] + 1)
            {
                kinds.Add(StrataColumnKind.Shared);
                ids.Add(left.BandId(leftIndex - 1));
                leftThicknesses.Add(left.BandThickness(leftIndex - 1));
                rightThicknesses.Add(right.BandThickness(rightIndex - 1));
                leftIndex--;
                rightIndex--;
            }
            else if (leftIndex > 0 && (rightIndex == 0 || lcs[leftIndex - 1, rightIndex] >= lcs[leftIndex, rightIndex - 1]))
            {
                kinds.Add(StrataColumnKind.LeftOnly);
                ids.Add(left.BandId(leftIndex - 1));
                leftThicknesses.Add(left.BandThickness(leftIndex - 1));
                rightThicknesses.Add(0.0f);
                leftIndex--;
            }
            else
            {
                kinds.Add(StrataColumnKind.RightOnly);
                ids.Add(right.BandId(rightIndex - 1));
                leftThicknesses.Add(0.0f);
                rightThicknesses.Add(right.BandThickness(rightIndex - 1));
                rightIndex--;
            }
        }

        kinds.Reverse();
        ids.Reverse();
        leftThicknesses.Reverse();
        rightThicknesses.Reverse();

        return new StrataTransition(
            kinds.ToArray(),
            ids.ToArray(),
            leftThicknesses.ToArray(),
            rightThicknesses.ToArray(),
            left,
            right
        );
    }

    /// <summary>
    ///     The id at <see cref="y"/> for a transition weight in [0, 1], where zero is the left table and one is the right.
    /// </summary>
    public byte Select(float w, float y)
    {
        var shareLeft = Window(1.0f - 2.0f * w);
        var shareRight = Window(2.0f * w - 1.0f);

        var height = y - _basementTopY;

        if (height < 0.0f)
        {
            return w < 0.5f ? _belowLeft : _belowRight;
        }

        for (var index = 0; index < _kinds.Length; index++)
        {
            var thickness = _kinds[index] switch
            {
                StrataColumnKind.Shared => _leftThicknesses[index] + (_rightThicknesses[index] - _leftThicknesses[index]) * w,
                StrataColumnKind.LeftOnly => _leftThicknesses[index] * shareLeft,
                _ => _rightThicknesses[index] * shareRight
            };

            if (height < thickness)
            {
                return _ids[index];
            }

            height -= thickness;
        }

        return w < 0.5f ? _topLeft : _topRight;
    }

    /// <summary>
    ///     The Hermite smooth step over an already clamped fraction, so a wedge closes without a crease at its tip.
    /// </summary>
    private static float Window(float value)
    {
        if (value <= 0.0f)
        {
            return 0.0f;
        }

        if (value >= 1.0f)
        {
            return 1.0f;
        }

        return value * value * (3.0f - 2.0f * value);
    }
}

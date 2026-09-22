namespace WinterGame.World.Generation;

/// <summary>
///     The geology provinces a banded table node selects among.
///     The province input is a continuous coordinate in [0, province count], whose integer values are the province boundaries.
///     A boundary blends the two adjacent tables over a fixed width, and the tables are aligned by id so mismatched beds pinch out instead of meeting at a vertical contact.
/// </summary>
public sealed class StrataSet
{
    public readonly StrataTable[] Tables;

    /// <summary>
    ///     Half the width of a province boundary, in province coordinates, so a boundary at integer <c>k</c> blends over <c>[k - half, k + half]</c>.
    ///     Zero is a hard fault, and the value cannot exceed a half, because then two boundaries would overlap.
    /// </summary>
    public readonly float TransitionHalfWidth;

    private readonly StrataTransition[] _transitions;

    public StrataSet(float transitionHalfWidth, params StrataTable[] tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Length == 0)
        {
            throw new ArgumentException("A strata set needs at least one province.", nameof(tables));
        }

        if (transitionHalfWidth is not (>= 0.0f and <= 0.5f))
        {
            throw new ArgumentOutOfRangeException(nameof(transitionHalfWidth), transitionHalfWidth, "The transition half width has to be in [0, 0.5].");
        }

        foreach (var table in tables)
        {
            ArgumentNullException.ThrowIfNull(table);

            if (!table.BasementTopY.Equals(tables[0].BasementTopY))
            {
                throw new ArgumentException("Every province has to share the basement top, because it is the datum the cover is measured from.", nameof(tables));
            }
        }

        Tables = tables;
        TransitionHalfWidth = transitionHalfWidth;
        _transitions = new StrataTransition[tables.Length - 1];

        for (var index = 0; index < _transitions.Length; index++)
        {
            _transitions[index] = StrataTransition.Align(tables[index], tables[index + 1]);
        }
    }

    public int ProvinceCount => Tables.Length;

    /// <summary>
    ///     The id at <see cref="y"/> in the province <see cref="province"/> selects, where the coordinate is clamped onto the first and last province.
    ///     Outside every transition band the table is chosen hard, and inside one the two tables are blended by their alignment.
    /// </summary>
    public byte Select(float province, float y)
    {
        if (Tables.Length == 1)
        {
            return Tables[0].Select(y);
        }

        var coordinate = Math.Clamp(province, 0.0f, Tables.Length);

        if (TransitionHalfWidth > 0.0f)
        {
            for (var index = 0; index < _transitions.Length; index++)
            {
                var low = index + 1 - TransitionHalfWidth;
                var high = index + 1 + TransitionHalfWidth;

                if (coordinate > low && coordinate < high)
                {
                    var fraction = (coordinate - low) / (high - low);

                    return _transitions[index].Select(fraction * fraction * (3.0f - 2.0f * fraction), y);
                }
            }
        }

        return Tables[Math.Clamp((int)MathF.Floor(coordinate), 0, Tables.Length - 1)].Select(y);
    }
}

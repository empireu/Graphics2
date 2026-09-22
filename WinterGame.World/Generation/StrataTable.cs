// ReSharper disable LoopCanBeConvertedToQuery

namespace WinterGame.World.Generation;

/// <summary>
///     One province's vertical sequence of material bands, stacked upward from the top of the basement.
///     The basement is the shared floor of every province, so it is the datum the cover is measured from and the place two provinces agree.
///     The lookup height may be warped by a fold noise, and the top band extends upward without limit.
/// </summary>
public sealed class StrataTable : IEquatable<StrataTable>
{
    /// <summary>
    ///     The top of the basement, in meters. Every province shares it, which is what keeps the deepest contact flat across a seam.
    /// </summary>
    public readonly float BasementTopY;

    /// <summary>
    ///     The id below the basement top, which is the basement itself and which extends downward without limit.
    /// </summary>
    public readonly byte BelowId;

    private readonly float[] _tops;
    private readonly byte[] _ids;

    /// <summary>
    ///     The <see cref="thicknesses"/> are measured upward from <see cref="basementTopY"/>, oldest first, and <see cref="ids"/> holds one id per band in the same order.
    /// </summary>
    public StrataTable(float basementTopY, byte belowId, ReadOnlySpan<float> thicknesses, ReadOnlySpan<byte> ids)
    {
        if (thicknesses.Length == 0)
        {
            throw new ArgumentException("A strata table needs at least one band", nameof(thicknesses));
        }

        if (thicknesses.Length != ids.Length)
        {
            throw new ArgumentException("A strata table has one id per band", nameof(ids));
        }

        if (!float.IsFinite(basementTopY))
        {
            throw new ArgumentException("The basement top has to be finite", nameof(basementTopY));
        }

        BasementTopY = basementTopY;
        BelowId = belowId;
        _ids = ids.ToArray();
        _tops = new float[thicknesses.Length];

        var height = 0.0f;

        for (var index = 0; index < thicknesses.Length; index++)
        {
            if (!(thicknesses[index] > 0.0f))
            {
                throw new ArgumentException("A band's thickness has to be positive", nameof(thicknesses));
            }

            height += thicknesses[index];
            _tops[index] = height;
        }
    }

    public int BandCount => _ids.Length;

    /// <summary>
    ///     The id of the band the height falls in.
    ///     Below the basement top is <see cref="BelowId"/> and everything above the last band is the youngest band.
    /// </summary>
    public byte Select(float y)
    {
        var height = y - BasementTopY;

        if (height < 0.0f)
        {
            return BelowId;
        }

        for (var index = 0; index < _tops.Length; index++)
        {
            if (height < _tops[index])
            {
                return _ids[index];
            }
        }

        return _ids[^1];
    }

    public byte BandId(int index) => _ids[index];

    public float BandThickness(int index) => index == 0 ? _tops[0] : _tops[index] - _tops[index - 1];

    /// <summary>
    ///     The height of a band's top, which is its cumulative thickness above the basement top.
    /// </summary>
    public float BandTop(int index) => BasementTopY + _tops[index];

    public bool Equals(StrataTable? other)
    {
        if (other == null || !BasementTopY.Equals(other.BasementTopY) || BelowId != other.BelowId || _ids.Length != other._ids.Length)
        {
            return false;
        }

        for (var index = 0; index < _ids.Length; index++)
        {
            if (_ids[index] != other._ids[index] || !_tops[index].Equals(other._tops[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as StrataTable);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(BasementTopY);
        hash.Add(BelowId);
        hash.Add(_ids.Length);

        for (var index = 0; index < _ids.Length; index++)
        {
            hash.Add(_ids[index]);
            hash.Add(_tops[index]);
        }

        return hash.ToHashCode();
    }
}

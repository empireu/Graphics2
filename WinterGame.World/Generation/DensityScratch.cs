namespace WinterGame.World.Generation;

/// <summary>
///     The buffers one <see cref="DensitySampler"/> evaluates into.
///     The graph and the sampler are immutable and safe to share, so one scratch per thread is what makes the sampler safe for concurrent readers, and reusing a scratch across fills is what keeps a worker allocation free.
/// </summary>
public sealed class DensityScratch
{
    internal readonly float[] CornerValues;
    internal readonly float[] SampleValues;

    /// <summary>
    ///     One value per root of the last point read.
    /// </summary>
    internal readonly float[] Roots;

    /// <summary>
    ///     The corner lattice of the last range fill, reused between fills and grown on demand.
    /// </summary>
    internal float[] CornerCache = [];

    private readonly float[][] _rangeOutputs;

    public DensityScratch(DensityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        CornerValues = new float[Math.Max(graph.CornerSlotCount, DensityGraph.FirstProgramSlot)];
        SampleValues = new float[graph.SampleSlotCount];
        Roots = new float[graph.RootCount];
        _rangeOutputs = new float[graph.RootCount][];

        for (var root = 0; root < _rangeOutputs.Length; root++)
        {
            _rangeOutputs[root] = [];
        }
    }

    // The size of the box the last range fill wrote:
    public int RangeCountX { get; private set; }
    public int RangeCountY { get; private set; }
    public int RangeCountZ { get; private set; }

    /// <summary>
    ///     One root's values from the last range fill, in x, then y, then z order, which is the flat order <see cref="Graphics2.Data.Span3D{T}"/> stores too.
    ///     A corner root has no per-sample value, so its buffer is left empty.
    /// </summary>
    public ReadOnlySpan<float> RangeOutput(int rootIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rootIndex);

        if (rootIndex >= _rangeOutputs.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rootIndex), rootIndex, "The root index is past the graph's root count.");
        }

        return _rangeOutputs[rootIndex].AsSpan(0, RangeCountX * RangeCountY * RangeCountZ);
    }

    internal void PrepareRange(int countX, int countY, int countZ)
    {
        RangeCountX = countX;
        RangeCountY = countY;
        RangeCountZ = countZ;

        var count = countX * countY * countZ;

        for (var root = 0; root < _rangeOutputs.Length; root++)
        {
            if (_rangeOutputs[root].Length < count)
            {
                _rangeOutputs[root] = new float[count];
            }
        }
    }

    internal float[] RangeOutputBuffer(int rootIndex) => _rangeOutputs[rootIndex];
}

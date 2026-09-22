using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation;

/// <summary>
///     A compiled density graph.
///     Has two instruction programs over slot buffers, the side tables they index, and one root per value the author asked for.
///     Every program's slots 0 to 2 are the position in meters, which the sampler writes before each evaluation.
///     With a cell boundary, the sample program also reserves the three fractions and the eight corner values.
///     The graph is immutable data, so it is safe to share, but a <see cref="DensitySampler"/> holds the buffers that evaluation needs.
/// </summary>
public sealed class DensityGraph
{
    // Reserved slots loaded up by the sampler:
    public const int PositionSlotX = 0;
    public const int PositionSlotY = 1;
    public const int PositionSlotZ = 2;
    public const int FractionSlotX = 3;
    public const int FractionSlotY = 4;
    public const int FractionSlotZ = 5;

    /// <summary>
    ///     The first of the eight cell corner values, packed with x in bit 0, y in bit 1 and z in bit 2, the same packing the mesher uses.
    /// </summary>
    public const int CornerSlotBase = 6;
    public const int CornerCount = 8;

    /// <summary>
    ///     The first slot a program allocates for its own instructions.
    /// </summary>
    public const int FirstProgramSlot = PositionSlotZ + 1;

    /// <summary>
    ///     The first slot the sample program allocates when a cell boundary reserved the fraction and corner slots.
    /// </summary>
    public const int FirstInterpolatedSampleSlot = CornerSlotBase + CornerCount;

    public readonly DensityInstruction[] CornerProgram;
    public readonly DensityInstruction[] SampleProgram;

    /// <summary>
    ///     The slot the corner program writes for the value the cell boundary interpolates, or <see cref="DensityInstruction.NoSlot" /> when the graph has no boundary.
    /// </summary>
    public readonly int BoundaryRoot;

    /// <summary>
    ///     The slot of each requested root in the corner program, or <see cref="DensityInstruction.NoSlot" /> when that root is evaluated per sample.
    /// </summary>
    public readonly int[] CornerRoots;

    /// <summary>
    ///     The slot of each requested root in the sample program, or <see cref="DensityInstruction.NoSlot" /> when that root is a corner root.
    /// </summary>
    public readonly int[] SampleRoots;

    /// <summary>
    ///     Whether each requested root is evaluated on the corner lattice instead of per sample.
    ///     A root below the boundary has no per-sample value, so a range fill leaves it alone and a point read runs the corner program at the exact position.
    /// </summary>
    public readonly bool[] RootIsCorner;

    public readonly int CornerSlotCount;
    public readonly int SampleSlotCount;

    /// <summary>
    ///     True when the graph was compiled with a cell boundary, so evaluation has to run the corner program on the cell lattice first.
    ///     It is not derived from the corner program being non-empty: a boundary over a bare position node emits no corner instruction, and its corner root is then a reserved slot.
    /// </summary>
    public readonly bool HasInterpolation;

    /// <summary>
    ///     True when at least one sample-side root reads the boundary, which is what makes filling the fraction and corner slots necessary.
    /// </summary>
    public readonly bool UsesBoundary;

    public readonly NoiseSpec[] Noises;
    public readonly float[] Constants;
    public readonly CubicSpline[] Splines;
    public readonly GradientSpec[] Gradients;
    public readonly StrataSet[] StrataSets;

    // The cell the graph interpolates over, in samples at the level being evaluated:
    public readonly int CellX;
    public readonly int CellY;
    public readonly int CellZ;

    internal DensityGraph(
        DensityInstruction[] cornerProgram,
        DensityInstruction[] sampleProgram,
        int boundaryRoot,
        int[] cornerRoots,
        int[] sampleRoots,
        bool[] rootIsCorner,
        int cornerSlotCount,
        int sampleSlotCount,
        NoiseSpec[] noises,
        float[] constants,
        CubicSpline[] splines,
        GradientSpec[] gradients,
        StrataSet[] strataSets,
        int cellX,
        int cellY,
        int cellZ,
        bool hasInterpolation,
        bool usesBoundary)
    {
        CornerProgram = cornerProgram;
        SampleProgram = sampleProgram;
        BoundaryRoot = boundaryRoot;
        CornerRoots = cornerRoots;
        SampleRoots = sampleRoots;
        RootIsCorner = rootIsCorner;
        CornerSlotCount = cornerSlotCount;
        SampleSlotCount = sampleSlotCount;
        Noises = noises;
        Constants = constants;
        Splines = splines;
        Gradients = gradients;
        StrataSets = strataSets;
        CellX = cellX;
        CellY = cellY;
        CellZ = cellZ;
        HasInterpolation = hasInterpolation;
        UsesBoundary = usesBoundary;
    }

    public int RootCount => RootIsCorner.Length;
}

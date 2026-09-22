using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation;

/// <summary>
///     A compiled density graph.
///     Has two instruction programs over slot buffers, the side tables they index, and the roots.
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
    ///     The address the corner program writes, or <see cref="DensityInstruction.NoSlot" /> when the graph has no cell boundary.
    /// </summary>
    public readonly int CornerRoot;
    public readonly int SampleRoot;
    public readonly int CornerSlotCount;
    public readonly int SampleSlotCount;

    /// <summary>
    ///     True when the graph was compiled with a cell boundary, so evaluation has to run the corner program on the cell lattice first.
    ///     It is not derived from the corner program being non-empty: a boundary over a bare position node emits no corner instruction, and its corner root is then a reserved slot.
    /// </summary>
    public readonly bool HasInterpolation;
    
    public readonly NoiseSpec[] Noises;
    public readonly float[] Constants;
    public readonly CubicSpline[] Splines;
    public readonly GradientSpec[] Gradients;

    // The cell the graph interpolates over, in samples at the level being evaluated:
    public readonly int CellX;
    public readonly int CellY;
    public readonly int CellZ;

    internal DensityGraph(
        DensityInstruction[] cornerProgram,
        DensityInstruction[] sampleProgram,
        int cornerRoot,
        int sampleRoot,
        int cornerSlotCount,
        int sampleSlotCount,
        NoiseSpec[] noises,
        float[] constants,
        CubicSpline[] splines,
        GradientSpec[] gradients,
        int cellX,
        int cellY,
        int cellZ,
        bool hasInterpolation)
    {
        CornerProgram = cornerProgram;
        SampleProgram = sampleProgram;
        CornerRoot = cornerRoot;
        SampleRoot = sampleRoot;
        CornerSlotCount = cornerSlotCount;
        SampleSlotCount = sampleSlotCount;
        Noises = noises;
        Constants = constants;
        Splines = splines;
        Gradients = gradients;
        CellX = cellX;
        CellY = cellY;
        CellZ = cellZ;
        HasInterpolation = hasInterpolation;
    }
}

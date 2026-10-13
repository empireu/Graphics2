using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation.GraphProgram;

/// <summary>
///     A compiled density graph.
///     Has two instruction programs over slot buffers, the side tables they index, and one root per value the author asked for.
///     Every program's slots 0 to 2 are the position in meters, which the sampler writes before each evaluation.
///     With cell boundaries, the sample program also reserves the three fractions, the cell's inverse lengths and eight corner values per boundary.
///     Each program starts with the instructions that do not depend on the height, so a range fill runs them once per column.
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

    // One over the cell's size in meters on each axis, which turns a gradient over the fractions into one over meters:
    public const int InverseCellLengthSlotX = 6;
    public const int InverseCellLengthSlotY = 7;
    public const int InverseCellLengthSlotZ = 8;

    /// <summary>
    ///     The first of each boundary's eight cell corner values, packed with x in bit 0, y in bit 1 and z in bit 2, the same packing the mesher uses.
    ///     Boundary k's corners start at <c>CornerSlotBase + k * CornerCount</c>.
    /// </summary>
    public const int CornerSlotBase = 9;
    public const int CornerCount = 8;

    /// <summary>
    ///     The first slot a program allocates for its own instructions.
    /// </summary>
    public const int FirstProgramSlot = PositionSlotZ + 1;

    /// <summary>
    ///     The length of the gradient <see cref="DensityOp.SurfaceDistance"/> divides by when the real one is shorter, and below which <see cref="DensityOp.SlopeCosine"/> calls the surface flat.
    /// </summary>
    public const float MinimumGradientLength = 1.0e-3f;

    public readonly DensityInstruction[] CornerProgram;
    public readonly DensityInstruction[] SampleProgram;

    // How many instructions at the start of each program read neither the height nor anything that does:
    public readonly int CornerColumnLength;
    public readonly int SampleColumnLength;

    /// <summary>
    ///     The slot the corner program writes for each boundary's value, in boundary order, which is the order the boundaries were authored in.
    ///     Empty when the graph has no boundary.
    /// </summary>
    public readonly int[] BoundaryRoots;

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
    ///     True when at least one sample-side root reads a boundary, which is what makes filling the fraction and corner slots necessary.
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
        int cornerColumnLength,
        int sampleColumnLength,
        int[] boundaryRoots,
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
        CornerColumnLength = cornerColumnLength;
        SampleColumnLength = sampleColumnLength;
        BoundaryRoots = boundaryRoots;
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

    public int BoundaryCount => BoundaryRoots.Length;

    /// <summary>
    ///     The first slot a sample program allocates for its own instructions when it reserves the cell slots of <paramref name="boundaryCount"/> boundaries.
    /// </summary>
    public static int FirstInterpolatedSampleSlot(int boundaryCount) => CornerSlotBase + boundaryCount * CornerCount;

    /// <summary>
    ///     The boundary whose interpolated value is a root's per-sample value, or <see cref="DensityInstruction.NoSlot" /> when the root is anything else.
    ///     That value is a weighted mean of its cell's corners and so never leaves their range.
    /// </summary>
    public int RootBoundary(int rootIndex)
    {
        if (RootIsCorner[rootIndex] || !UsesBoundary)
        {
            return DensityInstruction.NoSlot;
        }

        foreach (var instruction in SampleProgram)
        {
            if (instruction.Output == SampleRoots[rootIndex])
            {
                return instruction.Op == DensityOp.Interpolated ? instruction.Payload : DensityInstruction.NoSlot;
            }
        }

        return DensityInstruction.NoSlot;
    }
}

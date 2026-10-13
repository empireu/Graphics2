using System.Runtime.CompilerServices;
using Graphics2.Data;
using Graphics2.Mathematics;

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame.World.Generation.GraphProgram;

/// <summary>
///     Evaluates a compiled <see cref="DensityGraph"/> at sample positions and over sample boxes, with the cell interpolation the graph was compiled for.
///     The graph, the noise table and the side tables are immutable, so one sampler may be shared, but the buffers it evaluates into live in a <see cref="DensityScratch"/>, so a thread needs its own scratch.
/// </summary>
public sealed class DensitySampler
{
    /// <summary>
    ///     A column's view of the corner lattices, for the instructions that read their cell.
    /// </summary>
    private readonly struct ColumnCells(float[] cornerCache, int cornerCount, int strideY, int strideZ, int columnBase, int[] cellRows, float[] rowFractions)
    {
        public readonly float[] CornerCache = cornerCache;

        // One boundary's lattice is this long, and the next one follows it:
        public readonly int CornerCount = cornerCount;

        public readonly int StrideY = strideY;
        public readonly int StrideZ = strideZ;

        /// <summary>
        ///     The lattice index of the column's cell in the range's first cell row.
        /// </summary>
        public readonly int ColumnBase = columnBase;

        public readonly int[] CellRows = cellRows;
        public readonly float[] RowFractions = rowFractions;
    }

    private readonly FastNoiseLite[] _noises;
    private readonly DensityScratch _scratch;

    /// <summary>
    ///     Whether each root's slot changes along a column, so a range fill reads it from the lanes rather than from the column's one value.
    /// </summary>
    private readonly bool[] _rootVaries;

    /// <summary>
    ///     The column-invariant slots the per-sample instructions read, other than constants, which a range fill copies into the lanes once per column.
    /// </summary>
    private readonly int[] _columnInputs;

    // The constant slots the per-sample instructions read and their values, which a range fill copies into the lanes once:
    private readonly int[] _constantInputs;
    private readonly float[] _constantInputValues;

    /// <summary>
    ///     Creates a sampler with a scratch of its own, which makes it safe to use on one thread only.
    /// </summary>
    public DensitySampler(DensityGraph graph) : this(graph, new DensityScratch(graph))
    {
    }

    public DensitySampler(DensityGraph graph, DensityScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(scratch);

        Graph = graph;
        _scratch = scratch;
        _noises = new FastNoiseLite[graph.Noises.Length];

        for (var index = 0; index < _noises.Length; index++)
        {
            var spec = graph.Noises[index];
            var noise = new FastNoiseLite(spec.Seed);

            noise.SetNoiseType(spec.NoiseType);
            noise.SetFractalType(spec.FractalType);
            noise.SetFractalOctaves(spec.Octaves);
            noise.SetFrequency(spec.Frequency);

            _noises[index] = noise;
        }

        _rootVaries = new bool[graph.RootCount];

        var program = graph.SampleProgram;
        var varies = new bool[Math.Max(graph.SampleSlotCount, DensityGraph.FirstProgramSlot)];
        var constants = new float?[varies.Length];
        var inputs = new bool[varies.Length];

        varies[DensityGraph.PositionSlotY] = true;

        for (var index = 0; index < graph.SampleColumnLength; index++)
        {
            if (program[index].Op == DensityOp.Constant)
            {
                constants[program[index].Output] = graph.Constants[program[index].Payload];
            }
        }

        for (var index = graph.SampleColumnLength; index < program.Length; index++)
        {
            foreach (var input in new[] { program[index].Input0, program[index].Input1, program[index].Input2 })
            {
                if (input >= 0 && !varies[input])
                {
                    inputs[input] = true;
                }
            }

            varies[program[index].Output] = true;
        }

        var columnInputs = new List<int>();
        var constantInputs = new List<int>();
        var constantInputValues = new List<float>();

        for (var slot = 0; slot < inputs.Length; slot++)
        {
            if (!inputs[slot])
            {
                continue;
            }

            if (constants[slot] is { } value)
            {
                constantInputs.Add(slot);
                constantInputValues.Add(value);
            }
            else
            {
                columnInputs.Add(slot);
            }
        }

        _columnInputs = columnInputs.ToArray();
        _constantInputs = constantInputs.ToArray();
        _constantInputValues = constantInputValues.ToArray();

        for (var root = 0; root < graph.RootCount; root++)
        {
            _rootVaries[root] = !graph.RootIsCorner[root] && varies[graph.SampleRoots[root]];
        }
    }

    public DensityGraph Graph { get; }

    /// <summary>
    ///     The distance one sample means, which is the only place the LOD level enters the graph's meter coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleSpacing(int lod)
    {
        if (lod is < 0 or >= 30)
        {
            throw new ArgumentOutOfRangeException(nameof(lod), lod, "A LOD level has to be in [0, 30).");
        }

        return WorldConstants.VoxelSize * (1 << lod);
    }

    /// <summary>
    ///     Calculates the storage byte for one density sample.
    ///     The graph's root is a signed distance in meters, positive inside, so the byte is the iso level plus the distance in samples.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ContentFromDensity(float density, int lod)
    {
        var spacing = SampleSpacing(lod);

        return (byte)Math.Clamp(
            WorldConstants.ContentIsoLevel + density / spacing * WorldConstants.ContentBytesPerSample,
            0.0f,
            255.0f
        );
    }

    /// <summary>
    ///     The value of every root at one position, in the order the graph was compiled with.
    ///     The <see cref="position"/> is in sample indices at <see cref="lod"/>, so the world voxel it stands for is <c>position &lt;&lt; lod</c>.
    ///     A sample-side root inside a cell evaluates that cell's eight corners, because the corner lattice is what the interpolation is defined on, and a corner-side root runs the corner program at the exact position instead.
    /// </summary>
    public void Sample(Vector3i position, int lod, Span<float> roots)
    {
        if (roots.Length != Graph.RootCount)
        {
            throw new ArgumentException($"The graph has {Graph.RootCount} root(s), so the destination needs that many values.", nameof(roots));
        }

        var spacing = SampleSpacing(lod);
        var x = position.X * spacing;
        var y = position.Y * spacing;
        var z = position.Z * spacing;

        var values = _scratch.SampleValues;
        var cornerValues = _scratch.CornerValues;
        var boundaryRoots = Graph.BoundaryRoots;

        if (Graph.HasInterpolation && Graph.UsesBoundary)
        {
            var cellX = MathX.FloorDiv(position.X, Graph.CellX);
            var cellY = MathX.FloorDiv(position.Y, Graph.CellY);
            var cellZ = MathX.FloorDiv(position.Z, Graph.CellZ);

            for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
            {
                Run(
                    Graph.CornerProgram,
                    cornerValues,
                    (cellX + (corner & 1)) * Graph.CellX * spacing,
                    (cellY + ((corner >> 1) & 1)) * Graph.CellY * spacing,
                    (cellZ + ((corner >> 2) & 1)) * Graph.CellZ * spacing
                );

                for (var boundary = 0; boundary < boundaryRoots.Length; boundary++)
                {
                    values[DensityGraph.CornerSlotBase + boundary * DensityGraph.CornerCount + corner] = cornerValues[boundaryRoots[boundary]];
                }
            }

            values[DensityGraph.FractionSlotX] = (position.X - cellX * Graph.CellX) / (float)Graph.CellX;
            values[DensityGraph.FractionSlotY] = (position.Y - cellY * Graph.CellY) / (float)Graph.CellY;
            values[DensityGraph.FractionSlotZ] = (position.Z - cellZ * Graph.CellZ) / (float)Graph.CellZ;
            WriteInverseCellLengths(values, spacing);
        }
        else if (Graph.HasInterpolation)
        {
            Array.Clear(values, DensityGraph.CornerSlotBase, boundaryRoots.Length * DensityGraph.CornerCount);

            values[DensityGraph.FractionSlotX] = 0.0f;
            values[DensityGraph.FractionSlotY] = 0.0f;
            values[DensityGraph.FractionSlotZ] = 0.0f;
        }

        Run(Graph.SampleProgram, values, x, y, z);

        for (var root = 0; root < roots.Length; root++)
        {
            if (Graph.RootIsCorner[root])
            {
                // A corner root is the raw value below the boundary, so the corner program is run at the exact position rather than on the lattice:
                Run(Graph.CornerProgram, cornerValues, x, y, z);
                roots[root] = cornerValues[Graph.CornerRoots[root]];
            }
            else
            {
                roots[root] = values[Graph.SampleRoots[root]];
            }
        }
    }

    /// <summary>
    ///     One root at one position, which is the first root unless another is named.
    /// </summary>
    public float Sample(Vector3i position, int lod, int rootIndex = 0)
    {
        Sample(position, lod, _scratch.Roots);

        return _scratch.Roots[rootIndex];
    }

    /// <summary>
    ///     Fills the inclusive box [min, maxInclusive] with the value of every sample-side root.
    ///     The corners are computed once for the whole box and shared between the cells that meet there, so the fill is cheaper than a point read per sample.
    ///     The coordinates are in sample indices at <see cref="lod"/>.
    ///     A corner root has no per-sample value and is left alone: it needs to be read with <see cref="Sample(Vector3i, int, int)"/>.
    /// </summary>
    public void SampleRange(int lod, Vector3i min, Vector3i maxInclusive)
    {
        SampleRange(lod, min, maxInclusive, DensityInstruction.NoSlot, 0.0f);
    }

    /// <summary>
    ///     Fills the box like <see cref="SampleRange(int, Vector3i, Vector3i)"/> unless every corner value of <paramref name="boundary"/> is at or below <paramref name="level"/>, and then fills nothing and returns false.
    ///     A sample's interpolated value is a weighted mean of its cell's corners, so up to rounding it is at or below the level too.
    ///     A graph whose samples do not read a boundary is always filled.
    /// </summary>
    public bool SampleRangeIfAbove(int lod, Vector3i min, Vector3i maxInclusive, int boundary, float level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boundary);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(boundary, Graph.BoundaryCount);

        return SampleRange(lod, min, maxInclusive, boundary, level);
    }

    /// <summary>
    ///     The fill behind both public ones, which skips the box on <paramref name="skipBoundary"/>'s corners unless it is <see cref="DensityInstruction.NoSlot"/>.
    /// </summary>
    private bool SampleRange(int lod, Vector3i min, Vector3i maxInclusive, int skipBoundary, float level)
    {
        ValidateBox(min, maxInclusive);

        var countX = maxInclusive.X - min.X + 1;
        var countY = maxInclusive.Y - min.Y + 1;
        var countZ = maxInclusive.Z - min.Z + 1;

        var spacing = SampleSpacing(lod);
        var values = _scratch.SampleValues;
        var cornerValues = _scratch.CornerValues;

        if (!Graph.HasInterpolation || !Graph.UsesBoundary)
        {
            if (Graph.HasInterpolation)
            {
                values[DensityGraph.FractionSlotX] = 0.0f;
                values[DensityGraph.FractionSlotY] = 0.0f;
                values[DensityGraph.FractionSlotZ] = 0.0f;
            }

            FillColumns(spacing, min, countX, countY, countZ, 0, 0, 0, new ColumnCells([], 0, 0, 0, 0, [], []));

            return true;
        }

        var firstCellX = MathX.FloorDiv(min.X, Graph.CellX);
        var firstCellY = MathX.FloorDiv(min.Y, Graph.CellY);
        var firstCellZ = MathX.FloorDiv(min.Z, Graph.CellZ);

        var cornerCountX = MathX.FloorDiv(maxInclusive.X, Graph.CellX) - firstCellX + 2;
        var cornerCountY = MathX.FloorDiv(maxInclusive.Y, Graph.CellY) - firstCellY + 2;
        var cornerCountZ = MathX.FloorDiv(maxInclusive.Z, Graph.CellZ) - firstCellZ + 2;

        // One lattice per boundary, one after another:
        var cornerCount = cornerCountX * cornerCountY * cornerCountZ;
        var boundaryRoots = Graph.BoundaryRoots;

        if (_scratch.CornerCache.Length < cornerCount * boundaryRoots.Length)
        {
            _scratch.CornerCache = new float[cornerCount * boundaryRoots.Length];
        }

        var cornerCache = _scratch.CornerCache;

        // ReSharper disable once InlineTemporaryVariable
        var cornerStrideY = cornerCountX;
        var cornerStrideZ = cornerCountX * cornerCountY;

        var cornerProgram = Graph.CornerProgram;
        var cornerColumnLength = Graph.CornerColumnLength;
        var cornerMaximum = float.NegativeInfinity;

        for (var z = 0; z < cornerCountZ; z++)
        {
            cornerValues[DensityGraph.PositionSlotZ] = (firstCellZ + z) * Graph.CellZ * spacing;

            for (var x = 0; x < cornerCountX; x++)
            {
                cornerValues[DensityGraph.PositionSlotX] = (firstCellX + x) * Graph.CellX * spacing;
                Execute(cornerProgram, 0, cornerColumnLength, cornerValues);

                for (var y = 0; y < cornerCountY; y++)
                {
                    cornerValues[DensityGraph.PositionSlotY] = (firstCellY + y) * Graph.CellY * spacing;
                    Execute(cornerProgram, cornerColumnLength, cornerProgram.Length, cornerValues);

                    var latticeIndex = x + y * cornerStrideY + z * cornerStrideZ;

                    for (var boundary = 0; boundary < boundaryRoots.Length; boundary++)
                    {
                        cornerCache[boundary * cornerCount + latticeIndex] = cornerValues[boundaryRoots[boundary]];
                    }

                    if (skipBoundary >= 0)
                    {
                        cornerMaximum = Math.Max(cornerMaximum, cornerValues[boundaryRoots[skipBoundary]]);
                    }
                }
            }
        }

        if (skipBoundary >= 0 && cornerMaximum <= level)
        {
            return false;
        }

        WriteInverseCellLengths(values, spacing);
        FillColumns(spacing, min, countX, countY, countZ, firstCellX, firstCellY, firstCellZ, new ColumnCells(cornerCache, cornerCount, cornerStrideY, cornerStrideZ, 0, [], []));

        return true;
    }

    /// <summary>
    ///     Fills every sample-side root over the box one column at a time.
    ///     The column-invariant instructions run once per column into the scalar slots, and every other instruction runs over the whole column into its lanes, which costs one dispatch per instruction and column rather than per sample.
    ///     Both do the same arithmetic in the same order per sample as a point read, so the two agree bit for bit.
    ///     When the samples read a boundary, <paramref name="cells"/> holds the corner lattices, and its row arrays and column base are filled here.
    /// </summary>
    private void FillColumns(float spacing, Vector3i min, int countX, int countY, int countZ, int firstCellX, int firstCellY, int firstCellZ, ColumnCells cells)
    {
        var values = _scratch.SampleValues;
        var sampleProgram = Graph.SampleProgram;
        var sampleColumnLength = Graph.SampleColumnLength;
        var hasCells = Graph.HasInterpolation && Graph.UsesBoundary;

        _scratch.PrepareRange(countX, countY, countZ);
        _scratch.PrepareColumns(Graph.SampleSlotCount, countY);

        var lanes = _scratch.Lanes;
        var cellRows = _scratch.CellRows;
        var rowFractions = _scratch.RowFractions;

        for (var y = 0; y < countY; y++)
        {
            var worldY = min.Y + y;

            lanes[DensityGraph.PositionSlotY * countY + y] = worldY * spacing;

            if (hasCells)
            {
                var cellY = MathX.FloorDiv(worldY, Graph.CellY) - firstCellY;

                cellRows[y] = cellY;
                rowFractions[y] = (worldY - (cellY + firstCellY) * Graph.CellY) / (float)Graph.CellY;
            }
        }

        for (var index = 0; index < _constantInputs.Length; index++)
        {
            lanes.AsSpan(_constantInputs[index] * countY, countY).Fill(_constantInputValues[index]);
        }

        for (var z = 0; z < countZ; z++)
        {
            var worldZ = min.Z + z;
            var cellZ = MathX.FloorDiv(worldZ, Graph.CellZ) - firstCellZ;

            values[DensityGraph.PositionSlotZ] = worldZ * spacing;

            if (hasCells)
            {
                values[DensityGraph.FractionSlotZ] = (worldZ - (cellZ + firstCellZ) * Graph.CellZ) / (float)Graph.CellZ;
            }

            for (var x = 0; x < countX; x++)
            {
                var worldX = min.X + x;
                var cellX = MathX.FloorDiv(worldX, Graph.CellX) - firstCellX;

                values[DensityGraph.PositionSlotX] = worldX * spacing;

                if (hasCells)
                {
                    values[DensityGraph.FractionSlotX] = (worldX - (cellX + firstCellX) * Graph.CellX) / (float)Graph.CellX;
                }

                Execute(sampleProgram, 0, sampleColumnLength, values);

                for (var index = 0; index < _columnInputs.Length; index++)
                {
                    lanes.AsSpan(_columnInputs[index] * countY, countY).Fill(values[_columnInputs[index]]);
                }

                var columnCells = hasCells
                    ? new ColumnCells(cells.CornerCache, cells.CornerCount, cells.StrideY, cells.StrideZ, cellX + cellZ * cells.StrideZ, cellRows, rowFractions)
                    : cells;

                ExecuteLanes(sampleProgram, sampleColumnLength, sampleProgram.Length, values, lanes, countY, columnCells);
                WriteColumnRoots(values, lanes, countX, countY, x + z * countY * countX);
            }
        }
    }

    /// <summary>
    ///     Fills one root of the inclusive box into <see cref="destination"/>, which has to be sized exactly like the box.
    /// </summary>
    public void SampleRange(int lod, Vector3i min, Vector3i maxInclusive, Span3D<float> destination, int rootIndex = 0)
    {
        ValidateBox(min, maxInclusive);

        var countX = maxInclusive.X - min.X + 1;
        var countY = maxInclusive.Y - min.Y + 1;
        var countZ = maxInclusive.Z - min.Z + 1;

        if (destination.SizeX != countX || destination.SizeY != countY || destination.SizeZ != countZ)
        {
            throw new ArgumentException("The destination box has to be maxInclusive - min + 1 on every axis.", nameof(destination));
        }

        if (Graph.RootIsCorner[rootIndex])
        {
            throw new ArgumentException("A corner root has no per-sample value, so it cannot be filled over a box.", nameof(rootIndex));
        }

        SampleRange(lod, min, maxInclusive);

        var source = _scratch.RangeOutput(rootIndex);

        for (var z = 0; z < countZ; z++)
        {
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    destination[x, y, z] = source[x + y * countX + z * countX * countY];
                }
            }
        }
    }

    private static void ValidateBox(Vector3i min, Vector3i maxInclusive)
    {
        if (maxInclusive.X < min.X || maxInclusive.Y < min.Y || maxInclusive.Z < min.Z)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInclusive), "The box is empty.");
        }
    }

    /// <summary>
    ///     Writes the position and runs one program.
    ///     The fraction and corner slots are left alone, so the caller can fill them once per sample.
    /// </summary>
    private void Run(DensityInstruction[] program, float[] values, float x, float y, float z)
    {
        values[DensityGraph.PositionSlotX] = x;
        values[DensityGraph.PositionSlotY] = y;
        values[DensityGraph.PositionSlotZ] = z;

        Execute(program, 0, program.Length, values);
    }

    /// <summary>
    ///     Runs the instructions in [<paramref name="first"/>, <paramref name="end"/>) of a program over slots the caller has filled.
    /// </summary>
    private void Execute(DensityInstruction[] program, int first, int end, float[] values)
    {
        for (var index = first; index < end; index++)
        {
            ref readonly var instruction = ref program[index];
            var output = instruction.Output;

            values[output] = instruction.Op switch
            {
                DensityOp.Constant => Graph.Constants[instruction.Payload],
                DensityOp.Noise2D => _noises[instruction.Payload].GetNoise(values[instruction.Input0], values[instruction.Input1]),
                DensityOp.Noise3D => _noises[instruction.Payload].GetNoise(values[instruction.Input0], values[instruction.Input1], values[instruction.Input2]),
                DensityOp.Add => values[instruction.Input0] + values[instruction.Input1],
                DensityOp.Mul => values[instruction.Input0] * values[instruction.Input1],
                DensityOp.Clamp => Math.Clamp(values[instruction.Input0], values[instruction.Input1], values[instruction.Input2]),
                DensityOp.Min => Math.Min(values[instruction.Input0], values[instruction.Input1]),
                DensityOp.Max => Math.Max(values[instruction.Input0], values[instruction.Input1]),
                DensityOp.Abs => Math.Abs(values[instruction.Input0]),
                DensityOp.Div => values[instruction.Input0] / values[instruction.Input1],
                DensityOp.Sqrt => MathF.Sqrt(Math.Max(values[instruction.Input0], 0.0f)),
                DensityOp.Spline => Graph.Splines[instruction.Payload].Evaluate(values[instruction.Input0]),
                DensityOp.YClampedGradient => Graph.Gradients[instruction.Payload].Evaluate(values[instruction.Input0]),
                DensityOp.Blend => values[instruction.Input0] + (values[instruction.Input1] - values[instruction.Input0]) * values[instruction.Input2],
                DensityOp.Select => values[instruction.Input0] > 0.0f ? values[instruction.Input1] : values[instruction.Input2],
                DensityOp.BandedTable => Graph.StrataSets[instruction.Payload].Select(values[instruction.Input1], values[instruction.Input0]),
                DensityOp.Interpolated => Interpolate(values, DensityGraph.CornerSlotBase + instruction.Payload * DensityGraph.CornerCount),
                DensityOp.SlopeCosine => SlopeCosine(values, DensityGraph.CornerSlotBase + instruction.Payload * DensityGraph.CornerCount),
                DensityOp.SurfaceDistance => SurfaceDistance(values, DensityGraph.CornerSlotBase + instruction.Payload * DensityGraph.CornerCount),
                DensityOp.InputX => throw new InvalidOperationException($"{DensityOp.InputX} is not valid for execution"),
                DensityOp.InputY => throw new InvalidOperationException($"{DensityOp.InputY} is not valid for execution"),
                DensityOp.InputZ => throw new InvalidOperationException($"{DensityOp.InputZ} is not valid for execution"),
                _ => throw new InvalidOperationException($"Invalid opcode {instruction.Op}")
            };
        }
    }

    /// <summary>
    ///     The arithmetic of <see cref="DensityOp.Min"/>, <see cref="DensityOp.Max"/>, <see cref="DensityOp.Abs"/>, <see cref="DensityOp.Div"/> and <see cref="DensityOp.Sqrt"/> as <see cref="Execute"/> computes it, which the compiler folds constants with.
    ///     A unary opcode ignores <paramref name="right"/>.
    /// </summary>
    internal static float Evaluate(DensityOp op, float left, float right)
    {
        return op switch
        {
            DensityOp.Min => Math.Min(left, right),
            DensityOp.Max => Math.Max(left, right),
            DensityOp.Abs => Math.Abs(left),
            DensityOp.Div => left / right,
            DensityOp.Sqrt => MathF.Sqrt(Math.Max(left, 0.0f)),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic opcode with a fold.")
        };
    }

    private void WriteInverseCellLengths(float[] values, float spacing)
    {
        values[DensityGraph.InverseCellLengthSlotX] = 1.0f / (Graph.CellX * spacing);
        values[DensityGraph.InverseCellLengthSlotY] = 1.0f / (Graph.CellY * spacing);
        values[DensityGraph.InverseCellLengthSlotZ] = 1.0f / (Graph.CellZ * spacing);
    }

    /// <summary>
    ///     Runs the instructions in [<paramref name="first"/>, <paramref name="end"/>) of the sample program over one column, each over every sample of its lanes.
    ///     A lane is a slot's <paramref name="count"/> values, at <c>slot * count</c>, and every input a lane does not vary in has been copied in already.
    /// </summary>
    private void ExecuteLanes(DensityInstruction[] program, int first, int end, float[] values, float[] lanes, int count, in ColumnCells cells)
    {
        for (var index = first; index < end; index++)
        {
            ref readonly var instruction = ref program[index];
            var output = lanes.AsSpan(instruction.Output * count, count);

            switch (instruction.Op)
            {
                case DensityOp.Constant:
                    output.Fill(Graph.Constants[instruction.Payload]);
                    break;
                case DensityOp.Noise2D:
                {
                    var noise = _noises[instruction.Payload];
                    var x = Lane(lanes, instruction.Input0, count);
                    var z = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = noise.GetNoise(x[sample], z[sample]);
                    }

                    break;
                }
                case DensityOp.Noise3D:
                {
                    var noise = _noises[instruction.Payload];
                    var x = Lane(lanes, instruction.Input0, count);
                    var y = Lane(lanes, instruction.Input1, count);
                    var z = Lane(lanes, instruction.Input2, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = noise.GetNoise(x[sample], y[sample], z[sample]);
                    }

                    break;
                }
                case DensityOp.Add:
                {
                    var left = Lane(lanes, instruction.Input0, count);
                    var right = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = left[sample] + right[sample];
                    }

                    break;
                }
                case DensityOp.Mul:
                {
                    var left = Lane(lanes, instruction.Input0, count);
                    var right = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = left[sample] * right[sample];
                    }

                    break;
                }
                case DensityOp.Clamp:
                {
                    var value = Lane(lanes, instruction.Input0, count);
                    var min = Lane(lanes, instruction.Input1, count);
                    var max = Lane(lanes, instruction.Input2, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = Math.Clamp(value[sample], min[sample], max[sample]);
                    }

                    break;
                }
                case DensityOp.Min:
                {
                    var left = Lane(lanes, instruction.Input0, count);
                    var right = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = Math.Min(left[sample], right[sample]);
                    }

                    break;
                }
                case DensityOp.Max:
                {
                    var left = Lane(lanes, instruction.Input0, count);
                    var right = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = Math.Max(left[sample], right[sample]);
                    }

                    break;
                }
                case DensityOp.Abs:
                {
                    var value = Lane(lanes, instruction.Input0, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = Math.Abs(value[sample]);
                    }

                    break;
                }
                case DensityOp.Div:
                {
                    var left = Lane(lanes, instruction.Input0, count);
                    var right = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = left[sample] / right[sample];
                    }

                    break;
                }
                case DensityOp.Sqrt:
                {
                    var value = Lane(lanes, instruction.Input0, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = MathF.Sqrt(Math.Max(value[sample], 0.0f));
                    }

                    break;
                }
                case DensityOp.Spline:
                {
                    var spline = Graph.Splines[instruction.Payload];
                    var value = Lane(lanes, instruction.Input0, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = spline.Evaluate(value[sample]);
                    }

                    break;
                }
                case DensityOp.YClampedGradient:
                {
                    var gradient = Graph.Gradients[instruction.Payload];
                    var value = Lane(lanes, instruction.Input0, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = gradient.Evaluate(value[sample]);
                    }

                    break;
                }
                case DensityOp.Blend:
                {
                    var from = Lane(lanes, instruction.Input0, count);
                    var to = Lane(lanes, instruction.Input1, count);
                    var weight = Lane(lanes, instruction.Input2, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = from[sample] + (to[sample] - from[sample]) * weight[sample];
                    }

                    break;
                }
                case DensityOp.Select:
                {
                    var condition = Lane(lanes, instruction.Input0, count);
                    var ifPositive = Lane(lanes, instruction.Input1, count);
                    var ifNotPositive = Lane(lanes, instruction.Input2, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = condition[sample] > 0.0f ? ifPositive[sample] : ifNotPositive[sample];
                    }

                    break;
                }
                case DensityOp.BandedTable:
                {
                    var strata = Graph.StrataSets[instruction.Payload];
                    var height = Lane(lanes, instruction.Input0, count);
                    var province = Lane(lanes, instruction.Input1, count);

                    for (var sample = 0; sample < output.Length; sample++)
                    {
                        output[sample] = strata.Select(province[sample], height[sample]);
                    }

                    break;
                }
                case DensityOp.Interpolated or DensityOp.SlopeCosine or DensityOp.SurfaceDistance:
                    ExecuteCellLanes(instruction.Op, instruction.Payload, values, output, cells);
                    break;
                default:
                    throw new InvalidOperationException($"Invalid opcode {instruction.Op}");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<float> Lane(float[] lanes, int slot, int count)
    {
        return new ReadOnlySpan<float>(lanes, slot * count, count);
    }

    /// <summary>
    ///     One instruction that reads its cell over a column, reading the corners of each cell row from the boundary's lattice once.
    /// </summary>
    private static void ExecuteCellLanes(DensityOp op, int boundary, float[] values, Span<float> output, in ColumnCells cells)
    {
        var fractionX = values[DensityGraph.FractionSlotX];
        var fractionZ = values[DensityGraph.FractionSlotZ];
        var inverseLengthX = values[DensityGraph.InverseCellLengthSlotX];
        var inverseLengthY = values[DensityGraph.InverseCellLengthSlotY];
        var inverseLengthZ = values[DensityGraph.InverseCellLengthSlotZ];
        var latticeBase = boundary * cells.CornerCount + cells.ColumnBase;
        var cellRows = cells.CellRows.AsSpan(0, output.Length);
        var rowFractions = cells.RowFractions.AsSpan(0, output.Length);
        var loadedRow = -1;
        var corners = default(CellCorners);

        switch (op)
        {
            case DensityOp.Interpolated:
                for (var sample = 0; sample < output.Length; sample++)
                {
                    if (cellRows[sample] != loadedRow)
                    {
                        loadedRow = cellRows[sample];
                        corners = CellCorners.FromLattice(cells.CornerCache, latticeBase + loadedRow * cells.StrideY, cells.StrideY, cells.StrideZ);
                    }

                    output[sample] = corners.Blend(fractionX, rowFractions[sample], fractionZ);
                }

                break;
            case DensityOp.SlopeCosine:
                for (var sample = 0; sample < output.Length; sample++)
                {
                    if (cellRows[sample] != loadedRow)
                    {
                        loadedRow = cellRows[sample];
                        corners = CellCorners.FromLattice(cells.CornerCache, latticeBase + loadedRow * cells.StrideY, cells.StrideY, cells.StrideZ);
                    }

                    output[sample] = corners.SlopeCosine(fractionX, rowFractions[sample], fractionZ, inverseLengthX, inverseLengthY, inverseLengthZ);
                }

                break;
            default:
                for (var sample = 0; sample < output.Length; sample++)
                {
                    if (cellRows[sample] != loadedRow)
                    {
                        loadedRow = cellRows[sample];
                        corners = CellCorners.FromLattice(cells.CornerCache, latticeBase + loadedRow * cells.StrideY, cells.StrideY, cells.StrideZ);
                    }

                    output[sample] = corners.SurfaceDistance(fractionX, rowFractions[sample], fractionZ, inverseLengthX, inverseLengthY, inverseLengthZ);
                }

                break;
        }
    }

    /// <summary>
    ///     Copies a column's value of every sample-side root into the range outputs, which run x fastest, so a column's samples are <paramref name="countX"/> apart.
    /// </summary>
    private void WriteColumnRoots(float[] values, float[] lanes, int countX, int countY, int columnIndex)
    {
        for (var root = 0; root < Graph.RootCount; root++)
        {
            if (Graph.RootIsCorner[root])
            {
                continue;
            }

            var buffer = _scratch.RangeOutputBuffer(root);
            var slot = Graph.SampleRoots[root];

            if (_rootVaries[root])
            {
                var lane = Lane(lanes, slot, countY);

                for (var y = 0; y < countY; y++)
                {
                    buffer[columnIndex + y * countX] = lane[y];
                }
            }
            else
            {
                var value = values[slot];

                for (var y = 0; y < countY; y++)
                {
                    buffer[columnIndex + y * countX] = value;
                }
            }
        }
    }

    private static float Interpolate(float[] values, int cornerBase)
    {
        return CellCorners.FromSlots(values, cornerBase).Blend(values[DensityGraph.FractionSlotX], values[DensityGraph.FractionSlotY], values[DensityGraph.FractionSlotZ]);
    }

    private static float SlopeCosine(float[] values, int cornerBase)
    {
        return CellCorners.FromSlots(values, cornerBase).SlopeCosine(
            values[DensityGraph.FractionSlotX],
            values[DensityGraph.FractionSlotY],
            values[DensityGraph.FractionSlotZ],
            values[DensityGraph.InverseCellLengthSlotX],
            values[DensityGraph.InverseCellLengthSlotY],
            values[DensityGraph.InverseCellLengthSlotZ]
        );
    }

    private static float SurfaceDistance(float[] values, int cornerBase)
    {
        return CellCorners.FromSlots(values, cornerBase).SurfaceDistance(
            values[DensityGraph.FractionSlotX],
            values[DensityGraph.FractionSlotY],
            values[DensityGraph.FractionSlotZ],
            values[DensityGraph.InverseCellLengthSlotX],
            values[DensityGraph.InverseCellLengthSlotY],
            values[DensityGraph.InverseCellLengthSlotZ]
        );
    }

    /// <summary>
    ///     One boundary's eight cell corner values, packed like the corner slots, and the arithmetic every read of a cell shares, so a point read and a range fill agree bit for bit.
    /// </summary>
    private readonly struct CellCorners(float corner0, float corner1, float corner2, float corner3, float corner4, float corner5, float corner6, float corner7)
    {
        private readonly float _corner0 = corner0;
        private readonly float _corner1 = corner1;
        private readonly float _corner2 = corner2;
        private readonly float _corner3 = corner3;
        private readonly float _corner4 = corner4;
        private readonly float _corner5 = corner5;
        private readonly float _corner6 = corner6;
        private readonly float _corner7 = corner7;

        public static CellCorners FromSlots(float[] values, int cornerBase)
        {
            return new CellCorners(
                values[cornerBase],
                values[cornerBase + 1],
                values[cornerBase + 2],
                values[cornerBase + 3],
                values[cornerBase + 4],
                values[cornerBase + 5],
                values[cornerBase + 6],
                values[cornerBase + 7]
            );
        }

        public static CellCorners FromLattice(float[] lattice, int index, int strideY, int strideZ)
        {
            return new CellCorners(
                lattice[index],
                lattice[index + 1],
                lattice[index + strideY],
                lattice[index + 1 + strideY],
                lattice[index + strideZ],
                lattice[index + 1 + strideZ],
                lattice[index + strideY + strideZ],
                lattice[index + 1 + strideY + strideZ]
            );
        }

        /// <summary>
        ///     The trilinear blend, summed corner by corner from zero.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Blend(float fractionX, float fractionY, float fractionZ)
        {
            var value = 0.0f;

            value += _corner0 * (1.0f - fractionX) * (1.0f - fractionY) * (1.0f - fractionZ);
            value += _corner1 * fractionX * (1.0f - fractionY) * (1.0f - fractionZ);
            value += _corner2 * (1.0f - fractionX) * fractionY * (1.0f - fractionZ);
            value += _corner3 * fractionX * fractionY * (1.0f - fractionZ);
            value += _corner4 * (1.0f - fractionX) * (1.0f - fractionY) * fractionZ;
            value += _corner5 * fractionX * (1.0f - fractionY) * fractionZ;
            value += _corner6 * (1.0f - fractionX) * fractionY * fractionZ;
            value += _corner7 * fractionX * fractionY * fractionZ;

            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SlopeCosine(float fractionX, float fractionY, float fractionZ, float inverseLengthX, float inverseLengthY, float inverseLengthZ)
        {
            Gradient(fractionX, fractionY, fractionZ, inverseLengthX, inverseLengthY, inverseLengthZ, out var gradientX, out var gradientY, out var gradientZ);

            var lengthSquared = gradientX * gradientX + gradientY * gradientY + gradientZ * gradientZ;

            if (lengthSquared < DensityGraph.MinimumGradientLength * DensityGraph.MinimumGradientLength)
            {
                return 1.0f;
            }

            // The value is positive inside, so the outward normal is the negated gradient:
            return -gradientY / MathF.Sqrt(lengthSquared);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SurfaceDistance(float fractionX, float fractionY, float fractionZ, float inverseLengthX, float inverseLengthY, float inverseLengthZ)
        {
            Gradient(fractionX, fractionY, fractionZ, inverseLengthX, inverseLengthY, inverseLengthZ, out var gradientX, out var gradientY, out var gradientZ);

            var length = MathF.Sqrt(gradientX * gradientX + gradientY * gradientY + gradientZ * gradientZ);

            return Blend(fractionX, fractionY, fractionZ) / Math.Max(length, DensityGraph.MinimumGradientLength);
        }

        /// <summary>
        ///     The gradient in meters of the trilinear blend at the cell's fractions.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Gradient(
            float fractionX,
            float fractionY,
            float fractionZ,
            float inverseLengthX,
            float inverseLengthY,
            float inverseLengthZ,
            out float gradientX,
            out float gradientY,
            out float gradientZ)
        {
            // Each axis's derivative is its four edge differences weighted by the other two fractions:
            var alongX = ((_corner1 - _corner0) * (1.0f - fractionY) + (_corner3 - _corner2) * fractionY) * (1.0f - fractionZ)
                + ((_corner5 - _corner4) * (1.0f - fractionY) + (_corner7 - _corner6) * fractionY) * fractionZ;

            var alongY = ((_corner2 - _corner0) * (1.0f - fractionX) + (_corner3 - _corner1) * fractionX) * (1.0f - fractionZ)
                + ((_corner6 - _corner4) * (1.0f - fractionX) + (_corner7 - _corner5) * fractionX) * fractionZ;

            var alongZ = ((_corner4 - _corner0) * (1.0f - fractionX) + (_corner5 - _corner1) * fractionX) * (1.0f - fractionY)
                + ((_corner6 - _corner2) * (1.0f - fractionX) + (_corner7 - _corner3) * fractionX) * fractionY;

            gradientX = alongX * inverseLengthX;
            gradientY = alongY * inverseLengthY;
            gradientZ = alongZ * inverseLengthZ;
        }
    }
}

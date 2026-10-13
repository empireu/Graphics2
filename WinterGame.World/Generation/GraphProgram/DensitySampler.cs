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
    private readonly FastNoiseLite[] _noises;
    private readonly DensityScratch _scratch;

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
        var sampleProgram = Graph.SampleProgram;
        var sampleColumnLength = Graph.SampleColumnLength;

        if (!Graph.HasInterpolation || !Graph.UsesBoundary)
        {
            _scratch.PrepareRange(countX, countY, countZ);

            if (Graph.HasInterpolation)
            {
                values[DensityGraph.FractionSlotX] = 0.0f;
                values[DensityGraph.FractionSlotY] = 0.0f;
                values[DensityGraph.FractionSlotZ] = 0.0f;
            }

            for (var z = 0; z < countZ; z++)
            {
                values[DensityGraph.PositionSlotZ] = (min.Z + z) * spacing;

                for (var x = 0; x < countX; x++)
                {
                    values[DensityGraph.PositionSlotX] = (min.X + x) * spacing;
                    Execute(sampleProgram, 0, sampleColumnLength, values);

                    for (var y = 0; y < countY; y++)
                    {
                        values[DensityGraph.PositionSlotY] = (min.Y + y) * spacing;
                        Execute(sampleProgram, sampleColumnLength, sampleProgram.Length, values);
                        WriteRoots(values, x + (y + z * countY) * countX);
                    }
                }
            }

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

        _scratch.PrepareRange(countX, countY, countZ);
        WriteInverseCellLengths(values, spacing);

        for (var z = 0; z < countZ; z++)
        {
            var worldZ = min.Z + z;
            var cellZ = MathX.FloorDiv(worldZ, Graph.CellZ) - firstCellZ;

            values[DensityGraph.PositionSlotZ] = worldZ * spacing;
            values[DensityGraph.FractionSlotZ] = (worldZ - (cellZ + firstCellZ) * Graph.CellZ) / (float)Graph.CellZ;

            for (var x = 0; x < countX; x++)
            {
                var worldX = min.X + x;
                var cellX = MathX.FloorDiv(worldX, Graph.CellX) - firstCellX;

                values[DensityGraph.PositionSlotX] = worldX * spacing;
                values[DensityGraph.FractionSlotX] = (worldX - (cellX + firstCellX) * Graph.CellX) / (float)Graph.CellX;
                Execute(sampleProgram, 0, sampleColumnLength, values);

                // The corners change only when the column enters the next cell up:
                var loadedCellY = -1;

                for (var y = 0; y < countY; y++)
                {
                    var worldY = min.Y + y;
                    var cellY = MathX.FloorDiv(worldY, Graph.CellY) - firstCellY;

                    values[DensityGraph.PositionSlotY] = worldY * spacing;
                    values[DensityGraph.FractionSlotY] = (worldY - (cellY + firstCellY) * Graph.CellY) / (float)Graph.CellY;

                    if (cellY != loadedCellY)
                    {
                        loadedCellY = cellY;

                        var cornerIndex = cellX + cellY * cornerStrideY + cellZ * cornerStrideZ;

                        for (var boundary = 0; boundary < boundaryRoots.Length; boundary++)
                        {
                            var slotBase = DensityGraph.CornerSlotBase + boundary * DensityGraph.CornerCount;
                            var latticeBase = boundary * cornerCount + cornerIndex;

                            for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
                            {
                                var offset = (corner & 1) + ((corner >> 1) & 1) * cornerStrideY + ((corner >> 2) & 1) * cornerStrideZ;
                                values[slotBase + corner] = cornerCache[latticeBase + offset];
                            }
                        }
                    }

                    Execute(sampleProgram, sampleColumnLength, sampleProgram.Length, values);
                    WriteRoots(values, x + (y + z * countY) * countX);
                }
            }
        }

        return true;
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

    private void WriteRoots(float[] values, int index)
    {
        for (var root = 0; root < Graph.RootCount; root++)
        {
            if (!Graph.RootIsCorner[root])
            {
                _scratch.RangeOutputBuffer(root)[index] = values[Graph.SampleRoots[root]];
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Interpolate(float[] values, int cornerBase)
    {
        var fractionX = values[DensityGraph.FractionSlotX];
        var fractionY = values[DensityGraph.FractionSlotY];
        var fractionZ = values[DensityGraph.FractionSlotZ];

        var value = 0.0f;

        for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
        {
            var weightX = (corner & 1) != 0 ? fractionX : 1.0f - fractionX;
            var weightY = (corner & 2) != 0 ? fractionY : 1.0f - fractionY;
            var weightZ = (corner & 4) != 0 ? fractionZ : 1.0f - fractionZ;

            value += values[cornerBase + corner] * weightX * weightY * weightZ;
        }

        return value;
    }

    /// <summary>
    ///     The gradient in meters of the trilinear blend of the eight corners starting at <paramref name="cornerBase"/>, at the cell's fractions.
    /// </summary>
    private static void Gradient(float[] values, int cornerBase, out float gradientX, out float gradientY, out float gradientZ)
    {
        var fractionX = values[DensityGraph.FractionSlotX];
        var fractionY = values[DensityGraph.FractionSlotY];
        var fractionZ = values[DensityGraph.FractionSlotZ];

        var corner0 = values[cornerBase];
        var corner1 = values[cornerBase + 1];
        var corner2 = values[cornerBase + 2];
        var corner3 = values[cornerBase + 3];
        var corner4 = values[cornerBase + 4];
        var corner5 = values[cornerBase + 5];
        var corner6 = values[cornerBase + 6];
        var corner7 = values[cornerBase + 7];

        // Each axis's derivative is its four edge differences weighted by the other two fractions:
        var alongX = ((corner1 - corner0) * (1.0f - fractionY) + (corner3 - corner2) * fractionY) * (1.0f - fractionZ)
            + ((corner5 - corner4) * (1.0f - fractionY) + (corner7 - corner6) * fractionY) * fractionZ;

        var alongY = ((corner2 - corner0) * (1.0f - fractionX) + (corner3 - corner1) * fractionX) * (1.0f - fractionZ)
            + ((corner6 - corner4) * (1.0f - fractionX) + (corner7 - corner5) * fractionX) * fractionZ;

        var alongZ = ((corner4 - corner0) * (1.0f - fractionX) + (corner5 - corner1) * fractionX) * (1.0f - fractionY)
            + ((corner6 - corner2) * (1.0f - fractionX) + (corner7 - corner3) * fractionX) * fractionY;

        gradientX = alongX * values[DensityGraph.InverseCellLengthSlotX];
        gradientY = alongY * values[DensityGraph.InverseCellLengthSlotY];
        gradientZ = alongZ * values[DensityGraph.InverseCellLengthSlotZ];
    }

    private static float SlopeCosine(float[] values, int cornerBase)
    {
        Gradient(values, cornerBase, out var gradientX, out var gradientY, out var gradientZ);

        var lengthSquared = gradientX * gradientX + gradientY * gradientY + gradientZ * gradientZ;

        if (lengthSquared < DensityGraph.MinimumGradientLength * DensityGraph.MinimumGradientLength)
        {
            return 1.0f;
        }

        // The value is positive inside, so the outward normal is the negated gradient:
        return -gradientY / MathF.Sqrt(lengthSquared);
    }

    private static float SurfaceDistance(float[] values, int cornerBase)
    {
        Gradient(values, cornerBase, out var gradientX, out var gradientY, out var gradientZ);

        var length = MathF.Sqrt(gradientX * gradientX + gradientY * gradientY + gradientZ * gradientZ);

        return Interpolate(values, cornerBase) / Math.Max(length, DensityGraph.MinimumGradientLength);
    }
}

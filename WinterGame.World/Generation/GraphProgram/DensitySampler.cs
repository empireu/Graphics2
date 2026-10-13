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

                values[DensityGraph.CornerSlotBase + corner] = cornerValues[Graph.BoundaryRoot];
            }

            values[DensityGraph.FractionSlotX] = (position.X - cellX * Graph.CellX) / (float)Graph.CellX;
            values[DensityGraph.FractionSlotY] = (position.Y - cellY * Graph.CellY) / (float)Graph.CellY;
            values[DensityGraph.FractionSlotZ] = (position.Z - cellZ * Graph.CellZ) / (float)Graph.CellZ;
        }
        else if (Graph.HasInterpolation)
        {
            for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
            {
                values[DensityGraph.CornerSlotBase + corner] = 0.0f;
            }

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
        SampleRange(lod, min, maxInclusive, canSkip: false, 0.0f);
    }

    /// <summary>
    ///     Fills the box like <see cref="SampleRange(int, Vector3i, Vector3i)"/> unless every corner value of the boundary is at or below <paramref name="level"/>, and then fills nothing and returns false.
    ///     A sample's interpolated value is a weighted mean of its cell's corners, so up to rounding it is at or below the level too.
    ///     A graph whose samples do not read the boundary is always filled.
    /// </summary>
    public bool SampleRangeIfAbove(int lod, Vector3i min, Vector3i maxInclusive, float level)
    {
        return SampleRange(lod, min, maxInclusive, canSkip: true, level);
    }

    private bool SampleRange(int lod, Vector3i min, Vector3i maxInclusive, bool canSkip, float level)
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

        var cornerCount = cornerCountX * cornerCountY * cornerCountZ;

        if (_scratch.CornerCache.Length < cornerCount)
        {
            _scratch.CornerCache = new float[cornerCount];
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

                    var cornerValue = cornerValues[Graph.BoundaryRoot];

                    cornerCache[x + y * cornerStrideY + z * cornerStrideZ] = cornerValue;
                    cornerMaximum = Math.Max(cornerMaximum, cornerValue);
                }
            }
        }

        if (canSkip && cornerMaximum <= level)
        {
            return false;
        }

        _scratch.PrepareRange(countX, countY, countZ);

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

                for (var y = 0; y < countY; y++)
                {
                    var worldY = min.Y + y;
                    var cellY = MathX.FloorDiv(worldY, Graph.CellY) - firstCellY;

                    values[DensityGraph.PositionSlotY] = worldY * spacing;
                    values[DensityGraph.FractionSlotY] = (worldY - (cellY + firstCellY) * Graph.CellY) / (float)Graph.CellY;

                    var cornerIndex = cellX + cellY * cornerStrideY + cellZ * cornerStrideZ;

                    for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
                    {
                        var offset = (corner & 1) + ((corner >> 1) & 1) * cornerStrideY + ((corner >> 2) & 1) * cornerStrideZ;
                        values[DensityGraph.CornerSlotBase + corner] = cornerCache[cornerIndex + offset];
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
                DensityOp.Spline => Graph.Splines[instruction.Payload].Evaluate(values[instruction.Input0]),
                DensityOp.YClampedGradient => Graph.Gradients[instruction.Payload].Evaluate(values[instruction.Input0]),
                DensityOp.Blend => values[instruction.Input0] + (values[instruction.Input1] - values[instruction.Input0]) * values[instruction.Input2],
                DensityOp.Select => values[instruction.Input0] > 0.0f ? values[instruction.Input1] : values[instruction.Input2],
                DensityOp.BandedTable => Graph.StrataSets[instruction.Payload].Select(values[instruction.Input1], values[instruction.Input0]),
                DensityOp.Interpolated => Interpolate(values),
                DensityOp.InputX => throw new InvalidOperationException($"{DensityOp.InputX} is not valid for execution"),
                DensityOp.InputY => throw new InvalidOperationException($"{DensityOp.InputY} is not valid for execution"),
                DensityOp.InputZ => throw new InvalidOperationException($"{DensityOp.InputZ} is not valid for execution"),
                _ => throw new InvalidOperationException($"Invalid opcode {instruction.Op}")
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Interpolate(float[] values)
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

            value += values[DensityGraph.CornerSlotBase + corner] * weightX * weightY * weightZ;
        }

        return value;
    }
}

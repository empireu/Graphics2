using System.Diagnostics;
using System.Runtime.CompilerServices;
using Graphics2.Data;
using Graphics2.Mathematics;
// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame.World.Generation;

/// <summary>
///     Evaluates a compiled <see cref="DensityGraph"/> at sample positions and over sample boxes, with the cell interpolation the graph was compiled for.
///     Owns the slot and corner buffers it evaluates into, so it's not thread-safe.
/// </summary>
public sealed class DensitySampler
{
    private readonly FastNoiseLite[] _noises;
    private readonly float[] _cornerValues;
    private readonly float[] _sampleValues;
    private float[] _cornerCache = [];

    public DensitySampler(DensityGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        Graph = graph;
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

        _cornerValues = new float[Math.Max(graph.CornerSlotCount, DensityGraph.FirstProgramSlot)];
        _sampleValues = new float[graph.SampleSlotCount];
    }

    public DensityGraph Graph { get; }

    /// <summary>
    ///     The distance one sample means, which is the only place the LOD level enters the graph's meter coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleSpacing(int lod)
    {
        Debug.Assert(lod >= 0, $"Invalid LOD {lod}");
        Debug.Assert(lod < 30, $"LOD {lod} too large");
       
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
    ///     Samples a point. The <see cref="position"/> is in sample indices at <see cref="lod"/>, so the world voxel it stands for is <c>position &lt;&lt; lod</c>.
    ///     A point query inside a cell evaluates that cell's eight corners, because the corner lattice is what the interpolation is defined on.
    /// </summary>
    public float Sample(Vector3i position, int lod)
    {
        var spacing = SampleSpacing(lod);
        var x = position.X * spacing;
        var y = position.Y * spacing;
        var z = position.Z * spacing;

        if (!Graph.HasInterpolation)
        {
            Run(Graph.SampleProgram, _sampleValues, x, y, z);

            return _sampleValues[Graph.SampleRoot];
        }

        var cellX = MathX.FloorDiv(position.X, Graph.CellX);
        var cellY = MathX.FloorDiv(position.Y, Graph.CellY);
        var cellZ = MathX.FloorDiv(position.Z, Graph.CellZ);

        for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
        {
            Run(
                Graph.CornerProgram,
                _cornerValues,
                (cellX + (corner & 1)) * Graph.CellX * spacing,
                (cellY + ((corner >> 1) & 1)) * Graph.CellY * spacing,
                (cellZ + ((corner >> 2) & 1)) * Graph.CellZ * spacing
            );

            _sampleValues[DensityGraph.CornerSlotBase + corner] = _cornerValues[Graph.CornerRoot];
        }

        _sampleValues[DensityGraph.FractionSlotX] = (position.X - cellX * Graph.CellX) / (float)Graph.CellX;
        _sampleValues[DensityGraph.FractionSlotY] = (position.Y - cellY * Graph.CellY) / (float)Graph.CellY;
        _sampleValues[DensityGraph.FractionSlotZ] = (position.Z - cellZ * Graph.CellZ) / (float)Graph.CellZ;

        Run(Graph.SampleProgram, _sampleValues, x, y, z);

        return _sampleValues[Graph.SampleRoot];
    }

    /// <summary>
    ///     Fills the inclusive box <c>[min, maxInclusive]</c> with densities.
    ///     The coordinates and the destination are in sample indices at <paramref name="lod"/> (see <see cref="Sample"/>).
    ///     The corner lattice reaches up to one cell outside the box, so a range fill evaluates slightly more positions than it writes.
    /// </summary>
    public void SampleRange(int lod, Vector3i min, Vector3i maxInclusive, Span3D<float> density)
    {
        var countX = maxInclusive.X - min.X + 1;
        var countY = maxInclusive.Y - min.Y + 1;
        var countZ = maxInclusive.Z - min.Z + 1;

        if (countX < 1 || countY < 1 || countZ < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInclusive), "The box is empty.");
        }

        if (density.SizeX != countX || density.SizeY != countY || density.SizeZ != countZ)
        {
            throw new ArgumentException("The density box has to be maxInclusive - min + 1 on every axis.", nameof(density));
        }

        var spacing = SampleSpacing(lod);

        if (!Graph.HasInterpolation)
        {
            for (var z = 0; z < countZ; z++)
            {
                for (var y = 0; y < countY; y++)
                {
                    for (var x = 0; x < countX; x++)
                    {
                        Run(Graph.SampleProgram, _sampleValues, (min.X + x) * spacing, (min.Y + y) * spacing, (min.Z + z) * spacing);
                        density[x, y, z] = _sampleValues[Graph.SampleRoot];
                    }
                }
            }

            return;
        }

        var firstCellX = MathX.FloorDiv(min.X, Graph.CellX);
        var firstCellY = MathX.FloorDiv(min.Y, Graph.CellY);
        var firstCellZ = MathX.FloorDiv(min.Z, Graph.CellZ);

        var cornerCountX = MathX.FloorDiv(maxInclusive.X, Graph.CellX) - firstCellX + 2;
        var cornerCountY = MathX.FloorDiv(maxInclusive.Y, Graph.CellY) - firstCellY + 2;
        var cornerCountZ = MathX.FloorDiv(maxInclusive.Z, Graph.CellZ) - firstCellZ + 2;

        var cornerCount = cornerCountX * cornerCountY * cornerCountZ;

        if (_cornerCache.Length < cornerCount)
        {
            _cornerCache = new float[cornerCount];
        }

        // ReSharper disable once InlineTemporaryVariable
        var cornerStrideY = cornerCountX;
        var cornerStrideZ = cornerCountX * cornerCountY;

        for (var z = 0; z < cornerCountZ; z++)
        {
            for (var y = 0; y < cornerCountY; y++)
            {
                for (var x = 0; x < cornerCountX; x++)
                {
                    Run(
                        Graph.CornerProgram,
                        _cornerValues,
                        (firstCellX + x) * Graph.CellX * spacing,
                        (firstCellY + y) * Graph.CellY * spacing,
                        (firstCellZ + z) * Graph.CellZ * spacing
                    );

                    _cornerCache[x + y * cornerStrideY + z * cornerStrideZ] = _cornerValues[Graph.CornerRoot];
                }
            }
        }

        for (var z = 0; z < countZ; z++)
        {
            var worldZ = min.Z + z;
            var cellZ = MathX.FloorDiv(worldZ, Graph.CellZ) - firstCellZ;
            var fractionZ = (worldZ - (cellZ + firstCellZ) * Graph.CellZ) / (float)Graph.CellZ;

            for (var y = 0; y < countY; y++)
            {
                var worldY = min.Y + y;
                var cellY = MathX.FloorDiv(worldY, Graph.CellY) - firstCellY;
                var fractionY = (worldY - (cellY + firstCellY) * Graph.CellY) / (float)Graph.CellY;

                for (var x = 0; x < countX; x++)
                {
                    var worldX = min.X + x;
                    var cellX = MathX.FloorDiv(worldX, Graph.CellX) - firstCellX;
                    var fractionX = (worldX - (cellX + firstCellX) * Graph.CellX) / (float)Graph.CellX;

                    _sampleValues[DensityGraph.FractionSlotX] = fractionX;
                    _sampleValues[DensityGraph.FractionSlotY] = fractionY;
                    _sampleValues[DensityGraph.FractionSlotZ] = fractionZ;

                    var cornerIndex = cellX + cellY * cornerStrideY + cellZ * cornerStrideZ;

                    for (var corner = 0; corner < DensityGraph.CornerCount; corner++)
                    {
                        var offset = (corner & 1) + ((corner >> 1) & 1) * cornerStrideY + ((corner >> 2) & 1) * cornerStrideZ;
                        _sampleValues[DensityGraph.CornerSlotBase + corner] = _cornerCache[cornerIndex + offset];
                    }

                    Run(Graph.SampleProgram, _sampleValues, worldX * spacing, worldY * spacing, worldZ * spacing);

                    density[x, y, z] = _sampleValues[Graph.SampleRoot];
                }
            }
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

        for (var index = 0; index < program.Length; index++)
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

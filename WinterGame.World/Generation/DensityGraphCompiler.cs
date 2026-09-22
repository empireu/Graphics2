// ReSharper disable ConvertToPrimaryConstructor

using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation;

/// <summary>
///     Turns an authored graph into the compiled programs.
///     Does some analysis: reachability, constant folding, the cell split, slot allocation, dead instruction removal and the side tables.
///     Author order is dependency order, so the emission is one forward pass over the author list.
/// </summary>
internal static class DensityGraphCompiler
{
    public static DensityGraph Compile(DensityGraphBuilder builder, int rootIndex, int cellX, int cellY, int cellZ)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellZ);

        var emission = new Emission(builder);
        var boundary = emission.FindInterpolationBoundary(rootIndex);
        var hasInterpolation = boundary != DensityInstruction.NoSlot;

        var cornerProgram = Array.Empty<DensityInstruction>();
        var cornerRoot = DensityInstruction.NoSlot;
        var cornerSlotCount = 0;

        if (hasInterpolation)
        {
            cornerProgram = emission.Emit(
                builder.Nodes[boundary].Input0,
                DensityGraph.FirstProgramSlot,
                DensityInstruction.NoSlot,
                out cornerRoot,
                out cornerSlotCount
            );
        }

        var sampleProgram = emission.Emit(
            rootIndex,
            hasInterpolation ? DensityGraph.FirstInterpolatedSampleSlot : DensityGraph.FirstProgramSlot,
            boundary,
            out var sampleRoot,
            out var sampleSlotCount
        );

        var tables = new SideTables(builder, emission.Constants);
        cornerProgram = tables.Remap(cornerProgram);
        sampleProgram = tables.Remap(sampleProgram);

        return new DensityGraph(
            cornerProgram,
            sampleProgram,
            cornerRoot,
            sampleRoot,
            cornerSlotCount,
            sampleSlotCount,
            tables.Noises,
            tables.Constants,
            tables.Splines,
            tables.Gradients,
            cellX,
            cellY,
            cellZ,
            hasInterpolation
        );
    }

    /// <summary>
    ///     One program's emission. The folded-away and shared nodes are why the author index and the slot index are not the same number.
    /// </summary>
    private sealed class Emission
    {
        private readonly DensityGraphBuilder _graphBuilder;
        private readonly bool[] _reachable;
        private readonly int[] _slots;
        private readonly float[] _slotConstants;
        private readonly Dictionary<float, int> _constantSlots = [];
        private readonly Dictionary<float, int> _constantIndices = [];
        private readonly List<float> _constants = [];
        private readonly List<DensityInstruction> _program = [];
        private int _nextSlot;

        public Emission(DensityGraphBuilder builder)
        {
            _graphBuilder = builder;
            _reachable = new bool[builder.Nodes.Count];
            _slots = new int[builder.Nodes.Count];
            _slotConstants = new float[builder.Nodes.Count + DensityGraph.FirstInterpolatedSampleSlot];
        }

        /// <summary>
        ///     The compiled constants, one per distinct value, which may include values only constant folding produced.
        /// </summary>
        public IReadOnlyList<float> Constants => _constants;

        /// <summary>
        ///     The boundary node, or <see cref="DensityInstruction.NoSlot" /> when the graph has none.
        /// </summary>
        public int FindInterpolationBoundary(int rootIndex)
        {
            var visited = new bool[_graphBuilder.Nodes.Count];
            var pending = new Stack<int>();
            pending.Push(rootIndex);

            while (pending.Count > 0)
            {
                var index = pending.Pop();

                if (visited[index])
                {
                    continue;
                }

                visited[index] = true;

                var node = _graphBuilder.Nodes[index];

                if (node.Op == DensityOp.Interpolated)
                {
                    return index;
                }

                PushIfPresent(pending, node.Input0);
                PushIfPresent(pending, node.Input1);
                PushIfPresent(pending, node.Input2);
            }

            return DensityInstruction.NoSlot;
        }

        public DensityInstruction[] Emit(int rootIndex, int firstSlot, int boundaryIndex, out int rootSlot, out int slotCount)
        {
            MarkReachable(rootIndex, boundaryIndex);

            Array.Fill(_slots, DensityInstruction.NoSlot);
            Array.Fill(_slotConstants, float.NaN);
            _constantSlots.Clear();
            _program.Clear();
            _nextSlot = firstSlot;

            for (var index = 0; index < _graphBuilder.Nodes.Count; index++)
            {
                if (_reachable[index])
                {
                    _slots[index] = EmitNode(index);
                }
            }

            rootSlot = _slots[rootIndex];
            slotCount = _nextSlot;

            return PruneUnread(_program.ToArray(), rootSlot, slotCount);
        }

        /// <summary>
        ///     Everything reachable from the root, stopping at the boundary because its input subtree belongs to the corner program.
        /// </summary>
        private void MarkReachable(int rootIndex, int boundaryIndex)
        {
            Array.Fill(_reachable, false);

            var pending = new Stack<int>();
            pending.Push(rootIndex);

            while (pending.Count > 0)
            {
                var index = pending.Pop();

                if (_reachable[index])
                {
                    continue;
                }

                _reachable[index] = true;

                if (index == boundaryIndex)
                {
                    continue;
                }

                var node = _graphBuilder.Nodes[index];
                PushIfPresent(pending, node.Input0);
                PushIfPresent(pending, node.Input1);
                PushIfPresent(pending, node.Input2);
            }
        }

        private int EmitNode(int index)
        {
            var node = _graphBuilder.Nodes[index];

            return node.Op switch
            {
                DensityOp.InputX => DensityGraph.PositionSlotX,
                DensityOp.InputY => DensityGraph.PositionSlotY,
                DensityOp.InputZ => DensityGraph.PositionSlotZ,
                DensityOp.Constant => EmitConstant(_graphBuilder.Constants[node.Payload]),
                DensityOp.Add => EmitAdd(_slots[node.Input0], _slots[node.Input1]),
                DensityOp.Mul => EmitMul(_slots[node.Input0], _slots[node.Input1]),
                DensityOp.Clamp => EmitClamp(_slots[node.Input0], _slots[node.Input1], _slots[node.Input2]),
                DensityOp.Blend => EmitBlend(_slots[node.Input0], _slots[node.Input1], _slots[node.Input2]),
                DensityOp.Noise2D => EmitInstruction(DensityOp.Noise2D, _slots[node.Input0], _slots[node.Input1], DensityInstruction.NoSlot, node.Payload),
                DensityOp.Noise3D => EmitInstruction(DensityOp.Noise3D, _slots[node.Input0], _slots[node.Input1], _slots[node.Input2], node.Payload),
                DensityOp.Spline => EmitInstruction(DensityOp.Spline, _slots[node.Input0], DensityInstruction.NoSlot, DensityInstruction.NoSlot, node.Payload),
                DensityOp.YClampedGradient => EmitInstruction(DensityOp.YClampedGradient, _slots[node.Input0], DensityInstruction.NoSlot, DensityInstruction.NoSlot, node.Payload),
                DensityOp.Interpolated => EmitInstruction(DensityOp.Interpolated, DensityInstruction.NoSlot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, 0),
                _ => throw new InvalidOperationException($"Invalid opcode {node.Op}")
            };
        }

        private int EmitConstant(float value)
        {
            if (_constantSlots.TryGetValue(value, out var slot))
            {
                return slot;
            }

            if (!_constantIndices.TryGetValue(value, out var payload))
            {
                payload = _constants.Count;
                _constants.Add(value);
                _constantIndices[value] = payload;
            }

            slot = _nextSlot++;
            _slotConstants[slot] = value;
            _constantSlots[value] = slot;
            _program.Add(new DensityInstruction(DensityOp.Constant, slot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, payload));

            return slot;
        }

        private int EmitAdd(int left, int right)
        {
            if (IsConstant(left, out var leftValue) && IsConstant(right, out var rightValue))
            {
                return EmitConstant(leftValue + rightValue);
            }

            if (IsConstant(left, out leftValue) && leftValue == 0.0f)
            {
                return right;
            }

            if (IsConstant(right, out var rightZero) && rightZero == 0.0f)
            {
                return left;
            }

            return EmitInstruction(DensityOp.Add, left, right, DensityInstruction.NoSlot, 0);
        }

        private int EmitMul(int left, int right)
        {
            if (IsConstant(left, out var leftValue) && IsConstant(right, out var rightValue))
            {
                return EmitConstant(leftValue * rightValue);
            }

            if (IsConstant(left, out leftValue))
            {
                if (leftValue == 0.0f)
                {
                    return EmitConstant(0.0f);
                }

                if (leftValue.Equals(1.0f))
                {
                    return right;
                }
            }

            if (IsConstant(right, out var rightConstant))
            {
                if (rightConstant == 0.0f)
                {
                    return EmitConstant(0.0f);
                }

                if (rightConstant.Equals(1.0f))
                {
                    return left;
                }
            }

            return EmitInstruction(DensityOp.Mul, left, right, DensityInstruction.NoSlot, 0);
        }

        private int EmitClamp(int value, int min, int max)
        {
            if (IsConstant(value, out var valueConstant) && IsConstant(min, out var minConstant) && IsConstant(max, out var maxConstant))
            {
                return EmitConstant(Math.Clamp(valueConstant, minConstant, maxConstant));
            }

            return EmitInstruction(DensityOp.Clamp, value, min, max, 0);
        }

        private int EmitBlend(int from, int to, int weight)
        {
            if (IsConstant(weight, out var weightConstant))
            {
                if (weightConstant == 0.0f)
                {
                    return from;
                }

                if (weightConstant.Equals(1.0f))
                {
                    return to;
                }

                if (IsConstant(from, out var fromConstant) && IsConstant(to, out var toConstant))
                {
                    return EmitConstant(fromConstant + (toConstant - fromConstant) * weightConstant);
                }
            }

            return EmitInstruction(DensityOp.Blend, from, to, weight, 0);
        }

        private int EmitInstruction(DensityOp op, int input0, int input1, int input2, int payload)
        {
            var slot = _nextSlot++;
            _program.Add(new DensityInstruction(op, slot, input0, input1, input2, payload));

            return slot;
        }

        private bool IsConstant(int slot, out float value)
        {
            value = _slotConstants[slot];

            return !float.IsNaN(value);
        }

        /// <summary>
        ///     Drops every instruction whose slot is read by nobody.
        ///     Slots keep their numbers, so the pruned program has holes and <paramref name="slotCount"/> stays the bound.
        /// </summary>
        private static DensityInstruction[] PruneUnread(DensityInstruction[] program, int rootSlot, int slotCount)
        {
            var needed = new bool[slotCount];
            needed[rootSlot] = true;

            var live = new bool[program.Length];
            var liveCount = 0;

            for (var index = program.Length - 1; index >= 0; index--)
            {
                if (!needed[program[index].Output])
                {
                    continue;
                }

                live[index] = true;
                liveCount++;

                MarkInputsNeeded(needed, program[index]);
            }

            if (liveCount == program.Length)
            {
                return program;
            }

            var result = new DensityInstruction[liveCount];
            var cursor = 0;

            for (var index = 0; index < program.Length; index++)
            {
                if (live[index])
                {
                    result[cursor++] = program[index];
                }
            }

            return result;
        }

        private static void MarkInputsNeeded(bool[] needed, in DensityInstruction instruction)
        {
            if (instruction.Input0 >= 0)
            {
                needed[instruction.Input0] = true;
            }

            if (instruction.Input1 >= 0)
            {
                needed[instruction.Input1] = true;
            }

            if (instruction.Input2 >= 0)
            {
                needed[instruction.Input2] = true;
            }
        }

        private static void PushIfPresent(Stack<int> pending, int index)
        {
            if (index >= 0)
            {
                pending.Push(index);
            }
        }
    }

    /// <summary>
    ///     Rebuilds the side tables from the entries the surviving instructions read, one entry per distinct value.
    /// </summary>
    private sealed class SideTables(DensityGraphBuilder builder, IReadOnlyList<float> constants)
    {
        private readonly List<float> _constants = [];
        private readonly List<NoiseSpec> _noises = [];
        private readonly List<CubicSpline> _splines = [];
        private readonly List<GradientSpec> _gradients = [];
        private readonly Dictionary<float, int> _constantIndices = [];
        private readonly Dictionary<NoiseSpec, int> _noiseIndices = [];
        private readonly Dictionary<CubicSpline, int> _splineIndices = [];
        private readonly Dictionary<GradientSpec, int> _gradientIndices = [];

        public float[] Constants => _constants.ToArray();
        public NoiseSpec[] Noises => _noises.ToArray();
        public CubicSpline[] Splines => _splines.ToArray();
        public GradientSpec[] Gradients => _gradients.ToArray();

        public DensityInstruction[] Remap(DensityInstruction[] program)
        {
            var result = new DensityInstruction[program.Length];

            for (var index = 0; index < program.Length; index++)
            {
                var instruction = program[index];

                var payload = instruction.Op switch
                {
                    DensityOp.Constant => ConstantIndex(constants[instruction.Payload]),
                    DensityOp.Noise2D or DensityOp.Noise3D => NoiseIndex(builder.Noises[instruction.Payload]),
                    DensityOp.Spline => SplineIndex(builder.Splines[instruction.Payload]),
                    DensityOp.YClampedGradient => GradientIndex(builder.Gradients[instruction.Payload]),
                    _ => 0
                };

                result[index] = new DensityInstruction(instruction.Op, instruction.Output, instruction.Input0, instruction.Input1, instruction.Input2, payload);
            }

            return result;
        }

        private int ConstantIndex(float value)
        {
            if (_constantIndices.TryGetValue(value, out var index))
            {
                return index;
            }

            index = _constants.Count;
            _constants.Add(value);
            _constantIndices[value] = index;

            return index;
        }

        private int NoiseIndex(NoiseSpec spec)
        {
            if (_noiseIndices.TryGetValue(spec, out var index))
            {
                return index;
            }

            index = _noises.Count;
            _noises.Add(spec);
            _noiseIndices[spec] = index;

            return index;
        }

        private int SplineIndex(CubicSpline cubicSpline)
        {
            if (_splineIndices.TryGetValue(cubicSpline, out var index))
            {
                return index;
            }

            index = _splines.Count;
            _splines.Add(cubicSpline);
            _splineIndices[cubicSpline] = index;

            return index;
        }

        private int GradientIndex(GradientSpec gradient)
        {
            if (_gradientIndices.TryGetValue(gradient, out var index))
            {
                return index;
            }

            index = _gradients.Count;
            _gradients.Add(gradient);
            _gradientIndices[gradient] = index;

            return index;
        }
    }
}

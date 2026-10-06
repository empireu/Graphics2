using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation.GraphProgram;

internal static class DensityGraphCompiler
{
    public static DensityGraph Compile(DensityGraphBuilder builder, DensityNode[] roots, int cellX, int cellY, int cellZ)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellZ);

        var rootIndices = new int[roots.Length];

        for (var index = 0; index < roots.Length; index++)
        {
            rootIndices[index] = roots[index].Index;
        }

        var emission = new Emission(builder);
        var boundary = emission.FindInterpolationBoundary(rootIndices);
        var hasInterpolation = boundary != DensityInstruction.NoSlot;

        // A node below the boundary is only a corner value, so a root there is evaluated on the corner lattice rather than per sample:
        var rootIsCorner = new bool[roots.Length];
        var cornerInputs = new List<int>();

        if (hasInterpolation)
        {
            var boundaryInput = builder.Nodes[boundary].Input0;
            var cornerReachable = emission.ReachableFrom(boundaryInput);

            cornerInputs.Add(boundaryInput);

            for (var index = 0; index < rootIndices.Length; index++)
            {
                if (cornerReachable[rootIndices[index]])
                {
                    rootIsCorner[index] = true;
                    cornerInputs.Add(rootIndices[index]);
                }
            }
        }

        var sampleInputs = new List<int>();

        for (var index = 0; index < rootIndices.Length; index++)
        {
            if (!rootIsCorner[index])
            {
                sampleInputs.Add(rootIndices[index]);
            }
        }

        var cornerProgram = Array.Empty<DensityInstruction>();
        var cornerRootSlots = Array.Empty<int>();
        var boundaryRoot = DensityInstruction.NoSlot;
        var cornerSlotCount = 0;

        if (cornerInputs.Count > 0)
        {
            cornerProgram = emission.Emit(
                cornerInputs.ToArray(),
                DensityGraph.FirstProgramSlot,
                DensityInstruction.NoSlot,
                out cornerRootSlots,
                out cornerSlotCount
            );

            boundaryRoot = cornerRootSlots[0];
        }

        var sampleProgram = emission.Emit(
            sampleInputs.ToArray(),
            hasInterpolation ? DensityGraph.FirstInterpolatedSampleSlot : DensityGraph.FirstProgramSlot,
            boundary,
            out var sampleRootSlots,
            out var sampleSlotCount
        );

        var cornerRoots = new int[roots.Length];
        var sampleRoots = new int[roots.Length];

        for (var index = 0; index < rootIndices.Length; index++)
        {
            if (rootIsCorner[index])
            {
                var position = cornerInputs.IndexOf(rootIndices[index]);

                cornerRoots[index] = cornerRootSlots[position];
                sampleRoots[index] = DensityInstruction.NoSlot;
            }
            else
            {
                cornerRoots[index] = DensityInstruction.NoSlot;
                sampleRoots[index] = sampleRootSlots[sampleInputs.IndexOf(rootIndices[index])];
            }
        }

        var usesBoundary = false;

        foreach (var instruction in sampleProgram)
        {
            if (instruction.Op == DensityOp.Interpolated)
            {
                usesBoundary = true;
                break;
            }
        }

        var tables = new SideTables(builder, emission.Constants);
        cornerProgram = tables.Remap(cornerProgram);
        sampleProgram = tables.Remap(sampleProgram);

        return new DensityGraph(
            cornerProgram,
            sampleProgram,
            boundaryRoot,
            cornerRoots,
            sampleRoots,
            rootIsCorner,
            cornerSlotCount,
            sampleSlotCount,
            tables.Noises,
            tables.Constants,
            tables.Splines,
            tables.Gradients,
            tables.StrataSets,
            cellX,
            cellY,
            cellZ,
            hasInterpolation,
            usesBoundary
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
        public int FindInterpolationBoundary(int[] rootIndices)
        {
            var visited = new bool[_graphBuilder.Nodes.Count];
            var pending = new Stack<int>();

            foreach (var rootIndex in rootIndices)
            {
                pending.Push(rootIndex);
            }

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

        /// <summary>
        ///     Everything reachable from <see cref="rootIndex"/>, with no boundary stop, which is how a corner-side root is told apart from a sample-side one.
        /// </summary>
        public bool[] ReachableFrom(int rootIndex)
        {
            var reachable = new bool[_graphBuilder.Nodes.Count];
            var pending = new Stack<int>();

            pending.Push(rootIndex);

            while (pending.Count > 0)
            {
                var index = pending.Pop();

                if (reachable[index])
                {
                    continue;
                }

                reachable[index] = true;

                var node = _graphBuilder.Nodes[index];
                PushIfPresent(pending, node.Input0);
                PushIfPresent(pending, node.Input1);
                PushIfPresent(pending, node.Input2);
            }

            return reachable;
        }

        public DensityInstruction[] Emit(int[] rootIndices, int firstSlot, int boundaryIndex, out int[] rootSlots, out int slotCount)
        {
            MarkReachable(rootIndices, boundaryIndex);

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

            rootSlots = new int[rootIndices.Length];

            for (var index = 0; index < rootIndices.Length; index++)
            {
                rootSlots[index] = _slots[rootIndices[index]];
            }

            slotCount = _nextSlot;

            return PruneUnread(_program.ToArray(), rootSlots, slotCount);
        }

        /// <summary>
        ///     Everything reachable from the root set, stopping at the boundary because its input subtree belongs to the corner program.
        /// </summary>
        private void MarkReachable(int[] rootIndices, int boundaryIndex)
        {
            Array.Fill(_reachable, false);

            var pending = new Stack<int>();

            foreach (var rootIndex in rootIndices)
            {
                pending.Push(rootIndex);
            }

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
                DensityOp.Select => EmitSelect(_slots[node.Input0], _slots[node.Input1], _slots[node.Input2]),
                DensityOp.BandedTable => EmitInstruction(DensityOp.BandedTable, _slots[node.Input0], _slots[node.Input1], DensityInstruction.NoSlot, node.Payload),
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

        /// <summary>
        ///     Folds a constant condition, which is safe because the opcode picks one branch rather than interpolating between them.
        /// </summary>
        private int EmitSelect(int condition, int ifPositive, int ifNotPositive)
        {
            if (IsConstant(condition, out var conditionValue))
            {
                return conditionValue > 0.0f ? ifPositive : ifNotPositive;
            }

            return EmitInstruction(DensityOp.Select, condition, ifPositive, ifNotPositive, 0);
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
        ///     Slots keep their numbers, so the pruned program has holes and <paramref name="slotCount" /> stays the bound.
        /// </summary>
        private static DensityInstruction[] PruneUnread(DensityInstruction[] program, int[] rootSlots, int slotCount)
        {
            var needed = new bool[slotCount];

            foreach (var rootSlot in rootSlots)
            {
                if (rootSlot >= 0)
                {
                    needed[rootSlot] = true;
                }
            }

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
        private readonly List<StrataSet> _strataSets = [];
        private readonly Dictionary<float, int> _constantIndices = [];
        private readonly Dictionary<NoiseSpec, int> _noiseIndices = [];
        private readonly Dictionary<CubicSpline, int> _splineIndices = [];
        private readonly Dictionary<GradientSpec, int> _gradientIndices = [];
        private readonly Dictionary<StrataSet, int> _strataIndices = [];

        public float[] Constants => _constants.ToArray();
        public NoiseSpec[] Noises => _noises.ToArray();
        public CubicSpline[] Splines => _splines.ToArray();
        public GradientSpec[] Gradients => _gradients.ToArray();
        public StrataSet[] StrataSets => _strataSets.ToArray();

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
                    DensityOp.BandedTable => StrataIndex(builder.StrataSets[instruction.Payload]),
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

        private int StrataIndex(StrataSet strata)
        {
            if (_strataIndices.TryGetValue(strata, out var index))
            {
                return index;
            }

            index = _strataSets.Count;
            _strataSets.Add(strata);
            _strataIndices[strata] = index;

            return index;
        }
    }
}

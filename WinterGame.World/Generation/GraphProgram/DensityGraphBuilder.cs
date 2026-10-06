using Graphics2.Mathematics;
using Graphics2.Mathematics.Interpolation;

namespace WinterGame.World.Generation.GraphProgram;

/// <summary>
///     Authors a density graph and compiles it into a <see cref="DensityGraph"/>.
///     Every method returns a handle to a node that already exists, so the author list is always in dependency order and a cycle cannot be expressed.
///     A noise node's name is the salt its seed is derived from, so names have to be unique within one builder.
/// </summary>
public sealed class DensityGraphBuilder
{
    private readonly int _seed;
    private readonly List<AuthorNode> _nodes = [];
    private readonly List<NoiseSpec> _noises = [];
    private readonly List<CubicSpline> _splines = [];
    private readonly List<float> _constants = [];
    private readonly List<GradientSpec> _gradients = [];
    private readonly List<StrataSet> _strataSets = [];
    private readonly HashSet<string> _noiseNames = [];
    private bool _hasInterpolationBoundary;

    public DensityGraphBuilder(int seed)
    {
        _seed = seed;
        
        X = Append(new AuthorNode(DensityOp.InputX, "x", DensityInstruction.NoSlot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, 0));
        Y = Append(new AuthorNode(DensityOp.InputY, "y", DensityInstruction.NoSlot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, 0));
        Z = Append(new AuthorNode(DensityOp.InputZ, "z", DensityInstruction.NoSlot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, 0));
    }
    
    public readonly DensityNode X;
    public readonly DensityNode Y;
    public readonly DensityNode Z;

    public DensityNode Constant(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException($"Constant {value} is not valid", nameof(value));
        }

        var payload = _constants.Count;
        
        _constants.Add(value);

        return Append(new AuthorNode(DensityOp.Constant, "constant", DensityInstruction.NoSlot, DensityInstruction.NoSlot, DensityInstruction.NoSlot, payload));
    }
    
    public DensityNode Noise2D(
        string name,
        int? salt = null,
        FastNoiseLite.NoiseType noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
        FastNoiseLite.FractalType fractalType = FastNoiseLite.FractalType.FBm,
        int octaves = 4,
        float frequency = 1.0f)
    {
        return Noise2D(name, X, Z, salt, noiseType, fractalType, octaves, frequency);
    }

    /// <summary>
    ///     The <see cref="salt" /> fixes the noise's seed, so a stage keeps its field when its name or its neighbors change.
    ///     Without one, the salt is the name's hash.
    /// </summary>
    public DensityNode Noise2D(
        string name,
        DensityNode x,
        DensityNode z,
        int? salt = null,
        FastNoiseLite.NoiseType noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
        FastNoiseLite.FractalType fractalType = FastNoiseLite.FractalType.FBm,
        int octaves = 4,
        float frequency = 1.0f)
    {
        RequireOwn(x);
        RequireOwn(z);

        return AddNoise(name, DensityOp.Noise2D, x.Index, z.Index, DensityInstruction.NoSlot, salt, noiseType, fractalType, octaves, frequency);
    }

    public DensityNode Noise3D(
        string name,
        int? salt = null,
        FastNoiseLite.NoiseType noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
        FastNoiseLite.FractalType fractalType = FastNoiseLite.FractalType.FBm,
        int octaves = 4,
        float frequency = 1.0f)
    {
        return Noise3D(name, X, Y, Z, salt, noiseType, fractalType, octaves, frequency);
    }

    /// <summary>
    ///     The <see cref="salt"/> fixes the noise's seed, so a stage keeps its field when its name or its neighbors change.
    ///     Without one, the salt is the name's hash.
    /// </summary>
    public DensityNode Noise3D(
        string name,
        DensityNode x,
        DensityNode y,
        DensityNode z,
        int? salt = null,
        FastNoiseLite.NoiseType noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
        FastNoiseLite.FractalType fractalType = FastNoiseLite.FractalType.FBm,
        int octaves = 4,
        float frequency = 1.0f)
    {
        RequireOwn(x);
        RequireOwn(y);
        RequireOwn(z);

        return AddNoise(name, DensityOp.Noise3D, x.Index, y.Index, z.Index, salt, noiseType, fractalType, octaves, frequency);
    }

    public DensityNode Add(DensityNode left, DensityNode right)
    {
        RequireOwn(left);
        RequireOwn(right);

        return Append(new AuthorNode(DensityOp.Add, "add", left.Index, right.Index, DensityInstruction.NoSlot, 0));
    }

    public DensityNode Mul(DensityNode left, DensityNode right)
    {
        RequireOwn(left);
        RequireOwn(right);

        return Append(new AuthorNode(DensityOp.Mul, "mul", left.Index, right.Index, DensityInstruction.NoSlot, 0));
    }

    public DensityNode Clamp(DensityNode value, DensityNode min, DensityNode max)
    {
        RequireOwn(value);
        RequireOwn(min);
        RequireOwn(max);

        return Append(new AuthorNode(DensityOp.Clamp, "clamp", value.Index, min.Index, max.Index, 0));
    }

    public DensityNode Spline(DensityNode input, float[] positions, float[] values)
    {
        RequireOwn(input);

        var payload = _splines.Count;
        _splines.Add(new CubicSpline(positions, values));

        return Append(new AuthorNode(DensityOp.Spline, "spline", input.Index, DensityInstruction.NoSlot, DensityInstruction.NoSlot, payload));
    }

    public DensityNode YClampedGradient(DensityNode y, float fromY, float toY, float fromValue, float toValue)
    {
        RequireOwn(y);

        if (fromY.Equals(toY))
        {
            throw new ArgumentException("The gradient's two levels are equal", nameof(toY));
        }

        var payload = _gradients.Count;
        _gradients.Add(new GradientSpec(fromY, toY, fromValue, toValue));

        return Append(new AuthorNode(DensityOp.YClampedGradient, "y_clamped_gradient", y.Index, DensityInstruction.NoSlot, DensityInstruction.NoSlot, payload));
    }
    
    public DensityNode Blend(DensityNode from, DensityNode to, DensityNode weight)
    {
        RequireOwn(from);
        RequireOwn(to);
        RequireOwn(weight);

        return Append(new AuthorNode(DensityOp.Blend, "blend", from.Index, to.Index, weight.Index, 0));
    }

    public DensityNode Subtract(DensityNode left, DensityNode right) => Add(left, Mul(right, Constant(-1.0f)));

    /// <summary>
    ///     Picks <see cref="ifPositive"/> when <see cref="condition"/> is above zero and <see cref="ifNotPositive"/> otherwise, with no interpolation.
    ///     Used for discrete values.
    /// </summary>
    public DensityNode Select(DensityNode condition, DensityNode ifPositive, DensityNode ifNotPositive)
    {
        RequireOwn(condition);
        RequireOwn(ifPositive);
        RequireOwn(ifNotPositive);

        return Append(new AuthorNode(DensityOp.Select, "select", condition.Index, ifPositive.Index, ifNotPositive.Index, 0));
    }

    /// <summary>
    ///     The material bands of every province, looked up at <see cref="y"/> and chosen by the continuous <see cref="province"/> coordinate, whose integers are the province boundaries.
    /// </summary>
    public DensityNode BandedTable(DensityNode y, DensityNode province, StrataSet strata)
    {
        RequireOwn(y);
        RequireOwn(province);
        ArgumentNullException.ThrowIfNull(strata);

        var payload = _strataSets.Count;

        _strataSets.Add(strata);

        return Append(new AuthorNode(DensityOp.BandedTable, "banded_table", y.Index, province.Index, DensityInstruction.NoSlot, payload));
    }

    /// <summary>
    ///     Marks the cell boundary. Everything below is evaluated on the cell lattice and interpolated, and everything above is evaluated per sample.
    ///     A graph has at most one boundary.
    /// </summary>
    public DensityNode Interpolated(DensityNode value)
    {
        RequireOwn(value);

        if (_hasInterpolationBoundary)
        {
            throw new InvalidOperationException("Tried to create more than one boundary");
        }

        _hasInterpolationBoundary = true;

        return Append(new AuthorNode(DensityOp.Interpolated, "interpolated", value.Index, DensityInstruction.NoSlot, DensityInstruction.NoSlot, 0));
    }

    public DensityGraph Compile(DensityNode root, int cellX = 4, int cellY = 8, int cellZ = 4)
    {
        RequireOwn(root);

        return DensityGraphCompiler.Compile(this, [root], cellX, cellY, cellZ);
    }

    /// <summary>
    ///     Compiles a root set into one graph.
    ///     The roots share every subexpression they have in common, and a root that is only reachable below the cell boundary is a corner root, so a shape and its material can come out of one program.
    /// </summary>
    public DensityGraph Compile(DensityNode[] roots, int cellX = 4, int cellY = 8, int cellZ = 4)
    {
        ArgumentNullException.ThrowIfNull(roots);

        if (roots.Length == 0)
        {
            throw new ArgumentException("A graph needs at least one root.", nameof(roots));
        }

        foreach (var root in roots)
        {
            RequireOwn(root);
        }

        return DensityGraphCompiler.Compile(this, roots, cellX, cellY, cellZ);
    }

    internal IReadOnlyList<AuthorNode> Nodes => _nodes;
    internal IReadOnlyList<NoiseSpec> Noises => _noises;
    internal IReadOnlyList<CubicSpline> Splines => _splines;
    internal IReadOnlyList<float> Constants => _constants;
    internal IReadOnlyList<GradientSpec> Gradients => _gradients;
    internal IReadOnlyList<StrataSet> StrataSets => _strataSets;

    internal string NameOf(int index) => _nodes[index].Name;

    private DensityNode AddNoise(
        string name,
        DensityOp op,
        int input0,
        int input1,
        int input2,
        int? salt,
        FastNoiseLite.NoiseType noiseType,
        FastNoiseLite.FractalType fractalType,
        int octaves,
        float frequency)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(octaves);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        if (!_noiseNames.Add(name))
        {
            throw new ArgumentException($"The noise name '{name}' is already used, and the name is the salt its seed is derived from.", nameof(name));
        }

        var payload = _noises.Count;
        _noises.Add(new NoiseSpec(MathX.MixSeed(_seed, salt ?? MathX.Fnv1A(name)), noiseType, fractalType, octaves, frequency));

        return Append(new AuthorNode(op, name, input0, input1, input2, payload));
    }

    private DensityNode Append(AuthorNode node)
    {
        var index = _nodes.Count;
        _nodes.Add(node);

        return new DensityNode(this, index);
    }

    private void RequireOwn(DensityNode node)
    {
        if (!ReferenceEquals(node.GraphBuilder, this))
        {
            throw new ArgumentException("The node belongs to another builder.", nameof(node));
        }
    }
}

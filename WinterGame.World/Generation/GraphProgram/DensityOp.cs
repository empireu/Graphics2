namespace WinterGame.World.Generation.GraphProgram;

/// <summary>
///     The operations a density graph compiles to.
///     <see cref="InputX"/>, <see cref="InputY"/> and <see cref="InputZ"/> are mapped by the compiler to the reserved position slots and emits no instruction for them.
/// </summary>
public enum DensityOp : byte
{
    InputX,
    InputY,
    InputZ,
    Constant,
    Noise2D,
    Noise3D,
    Add,
    Mul,
    Clamp,
    Min,
    Max,
    Abs,

    /// <summary>
    ///     The first input over the second, with no guard: a zero divisor gives an infinity or NaN.
    /// </summary>
    Div,

    /// <summary>
    ///     The square root of the input clamped at zero, so a value rounded just below zero gives zero rather than NaN.
    /// </summary>
    Sqrt,

    Spline,
    YClampedGradient,
    Blend,

    /// <summary>
    ///     Discrete selector. The first input's sign picks the second input when it is positive and the third otherwise.
    ///     Discrete values, such as material IDs, have to go through this rather than <see cref="Blend"/>.
    /// </summary>
    Select,

    /// <summary>
    ///     A per-province list of material bands. The first input is the lookup height and the second is the province coordinate, a continuous value in [0, province count] whose integers are the province boundaries.
    /// </summary>
    BandedTable,

    /// <summary>
    ///     A cell boundary. Everything it reads is a corner program and everything above it is a sample program, so the opcode reads the reserved corner and fraction slots instead of operands.
    ///     The payload is the boundary's index, which picks its eight corner slots.
    /// </summary>
    Interpolated,

    /// <summary>
    ///     The cosine of the angle between up and the outward normal of a boundary's surface, taken from the gradient of its trilinear blend, for a value that is positive inside.
    ///     It is 1 on flat ground, 0 on a vertical face and negative under an overhang, and 1 where the gradient vanishes.
    ///     The payload is the boundary's index.
    /// </summary>
    SlopeCosine,

    /// <summary>
    ///     A boundary's interpolated value over the length of its gradient, which is the distance to the surface where the value is zero, to first order.
    ///     The gradient's length is clamped from below by <see cref="DensityGraph.MinimumGradientLength"/>.
    ///     The payload is the boundary's index.
    /// </summary>
    SurfaceDistance
}

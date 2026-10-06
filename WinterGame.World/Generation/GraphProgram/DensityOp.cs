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
    ///     The cell boundary. Everything it reads is a corner program and everything above it is a sample program, so the opcode reads the reserved corner and fraction slots instead of operands.
    /// </summary>
    Interpolated
}

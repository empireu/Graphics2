namespace WinterGame.World.Generation;

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
    ///     The cell boundary. Everything it reads is a corner program and everything above it is a sample program, so the opcode reads the reserved corner and fraction slots instead of operands.
    /// </summary>
    Interpolated
}

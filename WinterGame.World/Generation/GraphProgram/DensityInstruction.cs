namespace WinterGame.World.Generation.GraphProgram;

/// <summary>
///     One three-address instruction over a slot buffer.
///     The <see cref="Payload"/> indexes the graph's side table the opcode reads, and an unused input carries <see cref="NoSlot"/>.
/// </summary>
public readonly struct DensityInstruction(DensityOp op, int output, int input0, int input1, int input2, int payload)
{
    /// <summary>
    ///     The value an operand carries when the opcode does not read it.
    /// </summary>
    public const int NoSlot = -1;

    public readonly DensityOp Op = op;
    public readonly int Output = output;
    public readonly int Input0 = input0;
    public readonly int Input1 = input1;
    public readonly int Input2 = input2;
    public readonly int Payload = payload;

    public override string ToString() => $"{Op} [{Input0}, {Input1}, {Input2}] -> {Output}:{Payload}";
}

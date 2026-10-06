namespace WinterGame.World.Generation.GraphProgram;

/// <summary>
///     One authored node before compilation.
///     The <see cref="Payload"/> indexes the builder's side table for the opcode.
/// </summary>
internal readonly struct AuthorNode(DensityOp op, string name, int input0, int input1, int input2, int payload)
{
    public readonly DensityOp Op = op;
    public readonly string Name = name;
    public readonly int Input0 = input0;
    public readonly int Input1 = input1;
    public readonly int Input2 = input2;
    public readonly int Payload = payload;
}

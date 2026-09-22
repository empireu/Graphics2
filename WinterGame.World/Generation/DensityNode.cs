namespace WinterGame.World.Generation;

/// <summary>
///     A handle to one authored node.
///     It only refers to a node its own builder has already created, which is what makes a cycle inexpressible and keeps the author list in dependency order.
/// </summary>
public readonly struct DensityNode(DensityGraphBuilder builder, int index)
{
    internal readonly DensityGraphBuilder GraphBuilder = builder;
    internal readonly int Index = index;
    
    public override string ToString() => GraphBuilder == null ? "unbound" : GraphBuilder.NameOf(Index);
}

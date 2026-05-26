namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Surfaces the list of <see cref="MeshNode"/>s registered via
/// <c>MeshBuilder.AddMeshNodes(...)</c> as an <see cref="IStaticNodeProvider"/>.
/// Replaces the (deleted) <c>MeshConfiguration.Nodes</c> dictionary —
/// consumers iterate every <see cref="IStaticNodeProvider"/> in DI rather
/// than reaching into <see cref="MeshConfiguration"/> for a lookup table.
///
/// <para>Applies last-write-wins de-dup by <see cref="MeshNode.Path"/> at
/// iteration time, matching the semantics the dictionary provided via its
/// build-time <c>GroupBy(Path).Last()</c>.</para>
/// </summary>
internal sealed class StaticMeshNodeListProvider : IStaticNodeProvider
{
    private readonly IReadOnlyList<MeshNode> _nodes;

    public StaticMeshNodeListProvider(IReadOnlyList<MeshNode> nodes)
    {
        _nodes = nodes;
    }

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        if (_nodes.Count == 0)
            return Enumerable.Empty<MeshNode>();
        return _nodes
            .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());
    }
}

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
    private readonly ModuleContexts? _modules;

    public StaticMeshNodeListProvider(IReadOnlyList<MeshNode> nodes, ModuleContexts? modules = null)
    {
        _nodes = nodes;
        _modules = modules;
    }

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        if (_nodes.Count == 0)
            return Enumerable.Empty<MeshNode>();
        return WithCurrentModuleGenerations()
            .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());
    }

    /// <summary>
    /// The registered nodes, with every node a MODULE contributed served from that module's
    /// CURRENT generation (policy <c>module-live-update-default</c>): at the position its first
    /// boot-time node held, the current generation's nodes replace the ones the swapped-out
    /// generation contributed. With no swap this is the list exactly as registered.
    /// </summary>
    private IEnumerable<MeshNode> WithCurrentModuleGenerations()
    {
        if (_modules is null)
        {
            foreach (var node in _nodes)
                yield return node;
            yield break;
        }

        HashSet<string>? substituted = null;
        foreach (var node in _nodes)
        {
            if (_modules.OwnerOf(node) is not { } owner)
            {
                yield return node;
                continue;
            }
            var currentNodes = _modules.CurrentNodes(owner);
            if (currentNodes.Any(n => ReferenceEquals(n, node)))
            {
                yield return node;
                continue;
            }
            // A swapped-out generation's node: emit the current generation's nodes once, here.
            if ((substituted ??= new HashSet<string>(StringComparer.Ordinal)).Add(owner))
                foreach (var replacement in currentNodes)
                    yield return replacement;
        }
    }
}

namespace MeshWeaver.Mesh;

/// <summary>
/// Fluent visibility for a <see cref="MeshNode"/>: where it should NOT show up.
///
/// <para>The point is that a module states this next to its own registration, and a deployment
/// states it in its own configuration — instead of every surface maintaining a list of the things
/// it does not want to see. A home screen that enumerates what to hide is wrong the moment anyone
/// installs anything it has not heard of.</para>
///
/// <code>
/// // In a module's configuration:
/// .AddMeshNodes(new MeshNode("PluginRegistryCredential") { Name = "…" }
///     .HideFrom(MeshContexts.Search, MeshContexts.Create, MeshContexts.Content))
///
/// // In a deployment's final config (Systemorph/Memex), for something shipped by someone else —
/// // one call per node, on the node:
/// .AddMeshNodes(new MeshNode("Store/Plugin") { Name = "Plugin" }.HideFromContent())
/// </code>
///
/// <para>On a NodeType DEFINITION node the mark covers every instance of that type — one line hides
/// a whole family. On an ordinary node it covers just that node.</para>
/// </summary>
public static class MeshNodeVisibilityExtensions
{
    /// <summary>Adds the given contexts to this node's <see cref="MeshNode.ExcludeFromContext"/>.
    /// Additive and idempotent, so a module and a deployment can each contribute without one
    /// silently dropping the other's.</summary>
    public static MeshNode HideFrom(this MeshNode node, params string[] contexts)
    {
        if (contexts is not { Length: > 0 })
            return node;
        var merged = new HashSet<string>(
            node.ExcludeFromContext ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var context in contexts)
            if (!string.IsNullOrWhiteSpace(context))
                merged.Add(context);
        return node with { ExcludeFromContext = merged };
    }

    /// <summary>Keeps this node out of the lists people browse — the home screen and node children
    /// — while leaving search and create menus alone. <see cref="MeshContexts.Content"/>.</summary>
    public static MeshNode HideFromContent(this MeshNode node) => node.HideFrom(MeshContexts.Content);

    /// <summary>Infrastructure: a node that exists so the platform works, and that no one should
    /// ever meet while browsing, searching, or creating. Credentials, signing keys, ledgers,
    /// discovery records.</summary>
    public static MeshNode HideEverywhere(this MeshNode node) =>
        node.HideFrom(MeshContexts.Search, MeshContexts.Create, MeshContexts.Content);
}

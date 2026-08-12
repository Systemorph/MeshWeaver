using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// The NodeType path (e.g. <c>UWDeepfield/UwPortfolio</c>) whose compiled <c>HubConfiguration</c> is
/// being applied to this hub — carried on the <see cref="MessageHubConfiguration"/> so that
/// <c>MeshDataSource.WithContentType</c> can record the content type under an EXACT, mesh-unique key.
///
/// <para><b>Why it has to arrive ambiently.</b> <c>WithContentType</c> is called from a NodeType's
/// <c>configuration</c> lambda — C# stored in a JSON string on the node and compiled at runtime — so
/// its call sites live in mesh content, not in this repo. Adding a parameter there is not possible:
/// the sites are invisible to <c>dotnet build</c>, spread across private node repos, and would all
/// have to change at once. The hub configuration is the one channel that reaches the lambda without
/// touching it, and it is already used this way for <c>OwnNodeStreamHolder</c>.</para>
///
/// <para>🚨 <b>The instance hub's own path is NOT this value.</b> A per-node hub sits at the INSTANCE
/// path (<c>UWDeepfield/UwPortfolio/Instances/foo</c>); the NodeType path is what every instance of
/// that type shares, and it is what a reader has in hand as <see cref="MeshNode.NodeType"/>. That
/// correspondence — writer keyed by NodeType path, reader keyed by <c>node.NodeType</c> — is the
/// whole point: it is exact and mesh-unique, where a content type's bare CLR name is unique only
/// inside its own package.</para>
/// </summary>
/// <param name="Path">The NodeType's full path.</param>
public sealed record NodeTypePathHolder(string Path);

/// <summary>
/// Sets and reads <see cref="NodeTypePathHolder"/> on a hub configuration.
/// </summary>
public static class NodeTypePathExtensions
{
    /// <summary>
    /// Records the NodeType path a compiled <c>HubConfiguration</c> belongs to. Must be applied
    /// BEFORE that configuration runs, so the lambda's <c>WithContentType</c> can read it.
    /// No-op for a null/empty path, so a caller that genuinely has no NodeType (a plain node, the
    /// default config) stays unannotated rather than recording a meaningless key.
    /// </summary>
    public static MessageHubConfiguration WithNodeTypePath(
        this MessageHubConfiguration config, string? nodeTypePath)
        => string.IsNullOrWhiteSpace(nodeTypePath)
            ? config
            : config.Set(new NodeTypePathHolder(nodeTypePath));
}

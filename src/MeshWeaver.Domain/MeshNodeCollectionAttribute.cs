namespace MeshWeaver.Domain;

/// <summary>
/// Marks a collection property as containing references to MeshNodes.
/// The editor will render a MeshNodeCollectionControl with the specified queries.
/// Supports template variables in query strings:
/// - {node.namespace} — replaced with the current node's namespace at control creation time.
/// - {node.path} — replaced with the current node's full path at control creation time.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class MeshNodeCollectionAttribute(params string[] queries) : Attribute
{
    /// <summary>
    /// Query strings for the MeshNodeCollectionControl.
    /// Each query is run in parallel and results are merged.
    /// </summary>
    public string[] Queries { get; } = queries;

    /// <summary>
    /// Resolves template variables in query strings using the node object.
    /// Delegates to MeshNodeAttribute.ResolveQueries.
    /// </summary>
    public static string[] ResolveQueries(string[] queries, object? node)
        => MeshNodeAttribute.ResolveQueries(queries, node);

    /// <summary>
    /// Resolves template variables in query strings.
    /// Delegates to MeshNodeAttribute.ResolveQueries.
    /// </summary>
    public static string[] ResolveQueries(string[] queries, string? nodeNamespace, string? nodePath = null)
        => MeshNodeAttribute.ResolveQueries(queries, nodeNamespace, nodePath);
}

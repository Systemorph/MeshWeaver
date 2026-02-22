namespace MeshWeaver.Domain;

/// <summary>
/// Marks a property as a reference to a MeshNode.
/// The editor will render a MeshNodePickerControl with the specified queries.
/// Supports template variables in query strings:
/// - {node.namespace} — replaced with the current node's namespace at control creation time.
/// - {node.path} — replaced with the current node's full path at control creation time.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class MeshNodeAttribute(params string[] queries) : Attribute
{
    /// <summary>
    /// Query strings for the MeshNodePickerControl.
    /// Each query is run in parallel and results are merged.
    /// </summary>
    public string[] Queries { get; } = queries;

    /// <summary>
    /// Resolves template variables in query strings.
    /// Replaces {node.namespace} and {node.path} with actual values from the editing context.
    /// </summary>
    public static string[] ResolveQueries(string[] queries, string? nodeNamespace, string? nodePath = null)
    {
        if (queries == null || queries.Length == 0)
            return queries ?? [];

        return queries.Select(q =>
        {
            var resolved = q;
            if (nodeNamespace != null)
                resolved = resolved.Replace("{node.namespace}", nodeNamespace);
            if (nodePath != null)
                resolved = resolved.Replace("{node.path}", nodePath);
            return resolved;
        }).ToArray();
    }
}

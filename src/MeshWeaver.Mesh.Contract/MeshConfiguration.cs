using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Mesh;

/// <summary>
/// Configuration for the mesh including registered nodes.
/// Types are treated as mesh nodes with nodeType="NodeType".
/// </summary>
public class MeshConfiguration(
    IReadOnlyDictionary<string, MeshNode> meshNodes,
    Func<MessageHubConfiguration, MessageHubConfiguration>? defaultNodeHubConfiguration = null,
    IReadOnlyList<string>? globalCreatableTypes = null,
    IReadOnlySet<string>? autocompleteExcludedNodeTypes = null)
{
    /// <summary>
    /// Registered mesh nodes by their key/path.
    /// </summary>
    public IReadOnlyDictionary<string, MeshNode> Nodes { get; } = meshNodes;

    /// <summary>
    /// Default configuration applied to all node hubs.
    /// Use this to set up content collections, views, or other configuration
    /// that should be available on every node hub.
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration>? DefaultNodeHubConfiguration { get; } = defaultNodeHubConfiguration;

    /// <summary>
    /// Global types that are creatable everywhere by default.
    /// These include utility types like Markdown, Thread, and Agent.
    /// Can be overridden per-node using NodeTypeDefinition.ExcludeDefaults.
    /// </summary>
    public IReadOnlyList<string> GlobalCreatableTypes { get; } = globalCreatableTypes ?? DefaultGlobalCreatableTypes;

    /// <summary>
    /// Node types excluded from autocomplete/search results.
    /// Configured via MeshBuilder.AddAutocompleteExcludedTypes().
    /// Typically includes satellite types (Comment, Thread) and internal types (AccessAssignment, GroupMembership).
    /// </summary>
    public IReadOnlySet<string> AutocompleteExcludedNodeTypes { get; } = autocompleteExcludedNodeTypes ?? new HashSet<string>();

    /// <summary>
    /// Default global creatable types: Markdown, Thread, Agent, NodeType.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultGlobalCreatableTypes = ["Markdown", "Thread", "Agent", "NodeType"];

    /// <summary>
    /// Built from registered MeshNodes' ExcludeFromContext property.
    /// Key: context string (e.g., "search"), Value: set of node types excluded in that context.
    /// If a type definition node (e.g., path="Thread") has ExcludeFromContext={"search"},
    /// all instances with NodeType="Thread" are excluded when querying with context="search".
    /// </summary>
    private readonly Lazy<Dictionary<string, HashSet<string>>> _contextExcludedTypes = new(() =>
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in meshNodes.Values)
        {
            if (node.ExcludeFromContext == null || node.ExcludeFromContext.Count == 0)
                continue;

            foreach (var ctx in node.ExcludeFromContext)
            {
                if (!map.TryGetValue(ctx, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[ctx] = set;
                }
                set.Add(node.Path);
            }
        }
        return map;
    });

    /// <summary>
    /// Checks whether a given node type is excluded from a context.
    /// </summary>
    public bool IsExcludedFromContext(string? nodeType, string context)
    {
        if (string.IsNullOrEmpty(nodeType)) return false;
        return _contextExcludedTypes.Value.TryGetValue(context, out var excluded)
               && excluded.Contains(nodeType);
    }
}

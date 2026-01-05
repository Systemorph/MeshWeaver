using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using MeshWeaver.Data;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

public record SystemLog(
    string Service,
    string ServiceId,
    string Level,
    DateTimeOffset Timestamp,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, object>? Properties
)
{
    public long Id { get; init; }
}
public record MessageLog(
    string Service,
    string ServiceId,
    DateTimeOffset Timestamp,
    string Address,
    string MessageId,
    IReadOnlyDictionary<string, object?>? Message,
    string? Sender,
    string? Target,
    string? State,
    IReadOnlyDictionary<string, object?>? AccessContext,
    IReadOnlyDictionary<string, object?>? Properties)
{
    public long Id { get; init; }
}
/// <summary>
/// Represents a node in the mesh that can handle requests.
/// The Id is the local identifier within a namespace (e.g., "Root", "Alice", "Story1").
/// The Namespace is the container path (e.g., "type", "Systemorph/type/Project").
/// Path is derived as {Namespace}/{Id} and serves as the unique identifier.
/// For template nodes, AddressSegments determines how many path segments are used for hub addressing.
/// Score-based matching uses the Prefix (derived from Path) for pattern matching.
/// </summary>
public record MeshNode([property: Key] string Id, string? Namespace = null)
{
    /// <summary>
    /// The path for the built-in NodeType type definition node.
    /// Nodes with NodeType = NodeTypePath are type definitions.
    /// </summary>
    public const string NodeTypePath = "NodeType";

    /// <summary>
    /// The full path derived from Namespace and Id.
    /// For nodes without a namespace, this equals Id.
    /// </summary>
    public string Path => string.IsNullOrEmpty(Namespace) ? (Id) : $"{Namespace}/{Id}";

    /// <summary>
    /// Single segments as used for matching and addressing.
    /// </summary>
    [JsonIgnore, NotMapped]
    public readonly IReadOnlyList<string> Segments =
        string.IsNullOrEmpty(Namespace)
            ? (string.IsNullOrEmpty(Id) ? Array.Empty<string>() : Id.Split('/'))
            : Namespace.Split('/').Append(Id).ToArray();

    /// <summary>
    /// Extracts the prefix from a path.
    /// For template paths like "$template/graph/org/3", extracts "graph/org".
    /// For regular paths, returns the path as-is.
    /// </summary>
    private static string ExtractPrefix(string path)
    {

        // Format: $template/{prefix}/{segments}
        // e.g., "$template/graph/org/3" -> "graph/org"
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path.Substring(0, lastSlash) : path;
    }

    /// <summary>
    /// Creates a MeshNode from a full path by extracting Id and Namespace.
    /// E.g., "type/Root" becomes Id="Root", Namespace="type".
    /// </summary>
    public static MeshNode FromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path cannot be null or empty", nameof(path));

        var lastSlash = path.LastIndexOf('/');
        if (lastSlash < 0)
            return new MeshNode(path);

        var ns = path.Substring(0, lastSlash);
        var id = path.Substring(lastSlash + 1);
        return new MeshNode(id, ns);
    }

    public const string MeshIn = nameof(MeshIn);

    /// <summary>
    /// Human-readable name for display in UI.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The type/category of this node (e.g., "Northwind", "Todo", "Insurance").
    /// Used to identify the application type for routing and configuration.
    /// </summary>
    public string? NodeType { get; init; }

    /// <summary>
    /// Human-readable description of this mesh node for display in autocomplete and UI.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Category for grouping in catalog views.
    /// When set, overrides NodeType as the grouping title.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Controls how children are displayed in the catalog view.
    /// - "grouped": Group by category with headings (default for instances)
    /// - "hierarchical": Show parent-child relationships with indentation
    /// - null: Use default behavior based on node type
    /// </summary>
    public string? CatalogMode { get; init; }

    /// <summary>
    /// Override the default catalog query.
    /// Default is "namespace:{path}" (direct children only).
    /// Set to "namespace:{path} scope:descendants" to show all descendants.
    /// Supports any valid query syntax.
    /// </summary>
    public string? CatalogQuery { get; init; }

    /// <summary>
    /// Icon URL or identifier for display in UI.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Display order for sorting in autocomplete lists (lower values appear first).
    /// </summary>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// Number of segments to include in the address when creating hub instances.
    /// For example, prefix "pricing" with AddressSegments=3 means URLs like "pricing/Microsoft/2026"
    /// will create hubs with address "pricing/Microsoft/2026".
    /// If not set (0), defaults to the number of segments in the Prefix.
    /// </summary>
    public int AddressSegments { get; init; }

    /// <summary>
    /// Indicates this node's data is persisted and should be loaded on startup.
    /// When true, the hub will load children from IPersistenceService via MeshNodeTypeSource.
    /// </summary>
    public bool IsPersistent { get; init; }

    /// <summary>
    /// Timestamp when this node was last modified.
    /// Used for cache invalidation of dynamically compiled assemblies.
    /// When reading from file system, defaults to file's last modified time if not specified in JSON.
    /// </summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// The hub version when this node was last saved.
    /// Used to restore hub version on restart.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// The lifecycle state of this node.
    /// Transient nodes are awaiting hub confirmation.
    /// Active nodes have been validated and persisted.
    /// </summary>
    public MeshNodeState State { get; init; } = MeshNodeState.Active;

    /// <summary>
    /// Indicates this is a virtual node created from a template, not persisted.
    /// Virtual nodes are created on-demand when addressing template-based paths.
    /// CreateNodeRequest should not consider virtual nodes as "existing" nodes.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// The data model content for this node.
    /// The type depends on NodeType (e.g., Organization, Project, Story).
    /// </summary>
    public object? Content { get; init; }

    /// <summary>
    /// Gets the parent path for this node.
    /// Returns null for root-level nodes.
    /// </summary>
    [JsonIgnore, NotMapped]
    public string? ParentPath
    {
        get
        {
            var pathSegments = Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return pathSegments.Length <= 1
                ? null
                : string.Join("/", pathSegments.Take(pathSegments.Length - 1));
        }
    }

    public string? ThumbNail { get; init; }
    public string? StreamProvider { get; init; }
    [NotMapped]
    public string? AssemblyLocation { get; init; }
    /// <summary>
    /// Observable that emits the hub configuration function.
    /// Stored as IObservable to avoid blocking - only subscribe when actually creating the hub.
    /// Use FirstOrDefaultAsync() to get the value.
    /// </summary>
    [JsonIgnore, NotMapped]
    public IObservable<Func<MessageHubConfiguration, MessageHubConfiguration>?>? HubConfiguration { get; init; }
    public string? StartupScript
    {
        get; init;
    }

    public RoutingType RoutingType { get; init; }
    public InstantiationType InstantiationType { get; set; }


    [JsonIgnore, NotMapped]
    public ImmutableList<Func<IServiceCollection, IServiceCollection>> GlobalServiceConfigurations { get; set; } = [];

    public MeshNode WithGlobalServiceRegistry(Func<IServiceCollection, IServiceCollection> services)
        => this with { GlobalServiceConfigurations = GlobalServiceConfigurations.Add(services) };

    /// <summary>
    /// Function that returns the address to route autocomplete requests to.
    /// Receives the current AgentContext to determine the appropriate target.
    /// Returns null if autocomplete should not be routed.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Func<AgentContext?, Address?>? AutocompleteAddress { get; init; }
}

public enum InstantiationType
{
    HubConfiguration,
    Script
}

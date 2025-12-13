using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
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
/// The Path is the unique identifier for this node (e.g., "graph", "graph/org", "$template/graph/org/3").
/// For template nodes, AddressSegments determines how many path segments are used for hub addressing.
/// Score-based matching uses the Prefix (derived from Path) for pattern matching.
/// </summary>
public record MeshNode(string Path)
{
    /// <summary>
    /// The prefix used for score-based path matching.
    /// For regular nodes, this equals Path.
    /// For template nodes (paths starting with $), this is extracted from the path.
    /// </summary>
    [JsonIgnore, NotMapped]
    public string Prefix { get; init; } = ExtractPrefix(Path);

    /// <summary>
    /// The segments of the prefix, split by '/'.
    /// Used for score-based path matching.
    /// </summary>
    [JsonIgnore, NotMapped]
    public string[] Segments => Prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);

    [Key] public string Key { get; init; } = Path;

    /// <summary>
    /// Extracts the prefix from a path.
    /// For template paths like "$template/graph/org/3", extracts "graph/org".
    /// For regular paths, returns the path as-is.
    /// </summary>
    private static string ExtractPrefix(string path)
    {
        if (!path.StartsWith("$template/"))
            return path;

        // Format: $template/{prefix}/{segments}
        // e.g., "$template/graph/org/3" -> "graph/org"
        var withoutPrefix = path.Substring("$template/".Length);
        var lastSlash = withoutPrefix.LastIndexOf('/');
        return lastSlash > 0 ? withoutPrefix.Substring(0, lastSlash) : withoutPrefix;
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
    /// Icon name for display in UI (e.g., "Shield", "Database", "Folder").
    /// </summary>
    public string? IconName { get; init; }

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
    /// When true, the hub will issue LoadGraphRequest to load children from IPersistenceService.
    /// </summary>
    public bool IsPersistent { get; init; }

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
    public string? AssemblyLocation { get; init; }
    [JsonIgnore, NotMapped]
    public Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration { get; init; }
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

using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a system log entry for diagnostic and monitoring purposes.
/// </summary>
/// <param name="Service">The name of the service that generated the log.</param>
/// <param name="ServiceId">Unique identifier for the service instance.</param>
/// <param name="Level">Log level (e.g., Info, Warning, Error).</param>
/// <param name="Timestamp">When the log entry was created.</param>
/// <param name="Message">The log message content.</param>
/// <param name="Exception">Exception details if applicable.</param>
/// <param name="Properties">Additional contextual properties.</param>
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
    /// <summary>
    /// Unique identifier for the log entry.
    /// </summary>
    public long Id { get; init; }
}

/// <summary>
/// Represents a message log entry for tracking message flow in the mesh.
/// </summary>
/// <param name="Service">The name of the service that processed the message.</param>
/// <param name="ServiceId">Unique identifier for the service instance.</param>
/// <param name="Timestamp">When the message was logged.</param>
/// <param name="Address">The hub address that processed the message.</param>
/// <param name="MessageId">Unique identifier for the message.</param>
/// <param name="Message">The message content as key-value pairs.</param>
/// <param name="Sender">Address of the message sender.</param>
/// <param name="Target">Address of the message target.</param>
/// <param name="State">Current state of the message delivery.</param>
/// <param name="AccessContext">Security and access context information.</param>
/// <param name="Properties">Additional message properties.</param>
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
    /// <summary>
    /// Unique identifier for the message log entry.
    /// </summary>
    public long Id { get; init; }
}
/// <summary>
/// Represents a node in the mesh that can handle requests.
/// The Id is the local identifier within a namespace (e.g., "Root", "Alice", "Story1").
/// The Namespace is the container path (e.g., "type", "Systemorph/type/Project").
/// Path is derived as {Namespace}/{Id} and serves as the unique identifier.
/// </summary>
public record MeshNode([property: Key] string Id, [property: Editable(false)] string? Namespace = null)
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

    /// <summary>
    /// Constant identifying the mesh input.
    /// </summary>
    public const string MeshIn = nameof(MeshIn);

    /// <summary>
    /// Human-readable name for display in UI.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The type/category of this node (e.g., "Northwind", "Todo", "Insurance").
    /// Used to identify the application type for routing and configuration.
    /// </summary>
    [Editable(false)]
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
    /// Icon URL or identifier for display in UI.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Display order for sorting (lower values appear first, null values appear last).
    /// </summary>
    public int? DisplayOrder { get; init; }

    /// <summary>
    /// Timestamp when this node was last modified.
    /// Used for cache invalidation of dynamically compiled assemblies.
    /// When reading from file system, defaults to file's last modified time if not specified in JSON.
    /// </summary>
    [Editable(false)]
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// The hub version when this node was last saved.
    /// Used to restore hub version on restart.
    /// </summary>
    [Editable(false)]
    public long Version { get; init; }

    /// <summary>
    /// The lifecycle state of this node.
    /// Transient nodes are awaiting hub confirmation.
    /// Active nodes have been validated and persisted.
    /// </summary>
    [Editable(false)]
    public MeshNodeState State { get; init; } = MeshNodeState.Active;

    /// <summary>
    /// The data model content for this node.
    /// The type depends on NodeType (e.g., Organization, Project, Story).
    /// </summary>
    [Editable(false)]
    public object? Content { get; init; }

    /// <summary>
    /// File path to the dynamically compiled assembly for this node type.
    /// </summary>
    [NotMapped]
    public string? AssemblyLocation { get; init; }
    /// <summary>
    /// Hub configuration function that configures the message hub for this node.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration { get; init; }


    /// <summary>
    /// Gets or sets the global service configurations for this mesh node.
    /// </summary>
    [JsonIgnore, NotMapped]
    [Editable(false)]
    public ImmutableList<Func<IServiceCollection, IServiceCollection>> GlobalServiceConfigurations { get; set; } = [];

    /// <summary>
    /// Adds a global service registry configuration to this mesh node.
    /// </summary>
    /// <param name="services">Function to configure services.</param>
    /// <returns>A new MeshNode with the added service configuration.</returns>
    public MeshNode WithGlobalServiceRegistry(Func<IServiceCollection, IServiceCollection> services)
        => this with { GlobalServiceConfigurations = GlobalServiceConfigurations.Add(services) };
}


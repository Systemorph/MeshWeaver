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
/// The Prefix defines the URL path pattern this node matches (e.g., "app/Northwind", "pricing").
/// Score-based matching uses the number of segments in the prefix.
/// </summary>
public record MeshNode(string Prefix)
{
    /// <summary>
    /// The segments of the prefix, split by '/'.
    /// Used for score-based path matching.
    /// </summary>
    [JsonIgnore, NotMapped]
    public string[] Segments { get; } = Prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);

    [Key] public string Key { get; init; } = Prefix;
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

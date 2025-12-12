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
public record MeshNode(
    Address Address,
    string Name
)
{
    [Key] public string Key { get; init; } = Address.ToString();
    public const string MeshIn = nameof(MeshIn);

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
    public string? Namespace { get; init; }
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

}

public enum InstantiationType
{
    HubConfiguration,
    Script
}

/// <summary>
/// Represents a namespace in the mesh that can create MeshNodes for a specific address prefix.
/// Used for autocomplete to show available address types with their descriptions.
/// </summary>
public record MeshNamespace(
    string Prefix,
    string Name
)
{
    /// <summary>
    /// Human-readable description for display in autocomplete and UI.
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
    /// Minimum number of segments required for addresses of this namespace.
    /// Additional segments may be added based on autocomplete responses.
    /// Default is 1 (prefix + one segment).
    /// </summary>
    public int MinSegments { get; init; } = 1;

    /// <summary>
    /// Regex pattern for matching URL paths to this namespace.
    /// Should contain named capture group "address" for the full address string.
    /// If null, defaults to matching Prefix as first segment with MinSegments.
    /// Example: @"^(?&lt;address&gt;pricing/[^/]+/[^/]+)(?:/(?&lt;remainder&gt;.*))?$"
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Factory function that creates a MeshNode for a given address.
    /// Returns null if the address doesn't match this namespace.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Func<Address, MeshNode?>? Factory { get; init; }

    /// <summary>
    /// Function that returns the address to route autocomplete requests to.
    /// Receives the current AgentContext to determine the appropriate target.
    /// Returns null if autocomplete should not be routed.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Func<AgentContext?, Address?>? AutocompleteAddress { get; init; }
}

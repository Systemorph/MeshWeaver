using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Defines a data type that can be compiled at runtime from inline C# source.
/// Stored in _config/dataModels/{id}.json
/// </summary>
public record DataModel
{
    /// <summary>
    /// Unique identifier matching the NodeType (e.g., "story", "project").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Display name for UI (e.g., "Story", "Project").
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Icon name for UI (e.g., "Document", "Folder").
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// Description of the data type.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order for sorting in UI lists.
    /// </summary>
    public int DisplayOrder { get; init; }

    /// <summary>
    /// Inline C# source code defining the type(s).
    /// Example: "public record Story { [Key] public string Id { get; init; } ... }"
    /// Can include multiple types (e.g., record + enum) in a single source.
    /// </summary>
    public required string TypeSource { get; init; }

    /// <summary>
    /// Lambda expression for DataContext configuration.
    /// Example: "data => data.AddSource(src => src.WithType&lt;Story&gt;())"
    /// This allows configuring data sources, types with initialization, updates, etc.
    /// </summary>
    public string? DataContextConfiguration { get; init; }

    /// <summary>
    /// The compiled Type instance. Not serialized - populated at runtime after compilation.
    /// </summary>
    [JsonIgnore, NotMapped]
    public Type? CompiledType { get; set; }
}

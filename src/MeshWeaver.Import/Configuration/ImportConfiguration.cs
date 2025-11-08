using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// Base configuration for import operations.
/// Contains common properties shared across different import types.
/// </summary>
public class ImportConfiguration
{
    /// <summary>
    /// Unique identifier for this configuration (e.g., file name).
    /// </summary>
    [Key]
    public required string Name { get; init; }

    /// <summary>
    /// Entity identifier that this configuration applies to (e.g., PricingId, ProjectId, etc.).
    /// </summary>
    public required string EntityId { get; init; }
}

using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Import.Configuration;

/// <summary>
/// Base configuration for import operations.
/// Contains common properties shared across different import types.
/// </summary>
public class ImportConfiguration
{
    /// <summary>
    /// Unique identifier for this configuration (e.g., file name). Don't start with /.
    /// </summary>
    [Key]
    public required string Name { get; init; }

    /// <summary>
    /// Address to which this configuration applies (e.g., pricing/{pricingId}, project/{projectId}, etc.).
    /// </summary>
    public required string Address { get; init; }
}

using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Represents a reinsurance coverage section with layer structure and financial terms.
/// </summary>
public record ReinsuranceSection
{
    /// <summary>
    /// Gets or initializes the unique section identifier.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Gets or initializes the acceptanceId this section belongs to.
    /// </summary>
    public string? AcceptanceId { get; init; }

    /// <summary>
    /// Gets or initializes the section name or description.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or initializes the section type (e.g., "Fire Damage", "Natural Catastrophe", "Business Interruption").
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets or initializes the attachment point.
    /// </summary>
    public decimal Attach { get; init; }

    /// <summary>
    /// Gets or initializes the layer limit.
    /// </summary>
    public decimal Limit { get; init; }

    /// <summary>
    /// Gets or initializes the aggregate attachment point (annual aggregate deductible).
    /// </summary>
    public decimal? AggAttach { get; init; }

    /// <summary>
    /// Gets or initializes the aggregate limit (annual aggregate limit).
    /// </summary>
    public decimal? AggLimit { get; init; }
}

// <meshweaver>
// Id: ReinsuranceSection
// DisplayName: Reinsurance Section Data Model
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;


/// <summary>
/// Represents a reinsurance coverage section with layer structure and financial terms.
/// </summary>
public record ReinsuranceSection
{
    /// <summary>
    /// Gets or initializes the unique section identifier.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets or initializes the acceptanceId this section belongs to.
    /// </summary>
    [Browsable(false)]
    public string? AcceptanceId { get; init; }

    /// <summary>
    /// Gets or initializes the section name or description.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or initializes the section type (e.g., "Fire Damage", "Natural Catastrophe", "Business Interruption").
    /// </summary>
    [Dimension<LineOfBusiness>]
    [DisplayName("Line of Business")]
    public string? LineOfBusiness { get; init; }

    /// <summary>
    /// Gets or initializes the attachment point.
    /// </summary>
    [DisplayName("Attachment Point")]
    public decimal Attach { get; init; }

    /// <summary>
    /// Gets or initializes the layer limit.
    /// </summary>
    public decimal Limit { get; init; }

    /// <summary>
    /// Gets or initializes the aggregate attachment point (annual aggregate deductible).
    /// </summary>
    [DisplayName("Aggregate Attachment")]
    public decimal? AggAttach { get; init; }

    /// <summary>
    /// Gets or initializes the aggregate limit (annual aggregate limit).
    /// </summary>
    [DisplayName("Aggregate Limit")]
    public decimal? AggLimit { get; init; }
}

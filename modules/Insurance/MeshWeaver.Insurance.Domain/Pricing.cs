using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Represents an insurance pricing entity with dimension-based classification.
/// </summary>
public record Pricing
{
    /// <summary>
    /// Unique identifier for the pricing record.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// The date when the insurance coverage begins.
    /// </summary>
    public DateTime? InceptionDate { get; init; }

    /// <summary>
    /// The date when the insurance coverage ends.
    /// </summary>
    public DateTime? ExpirationDate { get; init; }

    /// <summary>
    /// The underwriting year for this pricing.
    /// </summary>
    public int? UnderwritingYear { get; init; }

    /// <summary>
    /// Line of business classification (dimension).
    /// </summary>
    [Dimension<LineOfBusiness>]
    public string? LineOfBusiness { get; init; }

    /// <summary>
    /// Country code or name (dimension).
    /// </summary>
    [Dimension<Country>]
    public string? Country { get; init; }

    /// <summary>
    /// Legal entity identifier (dimension).
    /// </summary>
    [Dimension<LegalEntity>]
    public string? LegalEntity { get; init; }

    /// <summary>
    /// Name of the insured party.
    /// </summary>
    public string? InsuredName { get; init; }

    /// <summary>
    /// Name of the broker handling this pricing.
    /// </summary>
    public string? BrokerName { get; init; }

    /// <summary>
    /// Premium amount in the pricing currency.
    /// </summary>
    public decimal? Premium { get; init; }

    /// <summary>
    /// Currency code for the premium.
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// Current status of the pricing (e.g., Draft, Quoted, Bound).
    /// </summary>
    public string? Status { get; init; }
}

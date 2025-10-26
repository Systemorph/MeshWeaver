using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Represents the reinsurance layer structure and financial terms.
/// </summary>
public record Structure
{
    /// <summary>
    /// Gets or initializes the unique layer identifier.
    /// </summary>
    [Key]
    public required string LayerId { get; init; }

    /// <summary>
    /// Gets or initializes the pricing/contract ID this structure belongs to.
    /// </summary>
    public string? PricingId { get; init; }

    /// <summary>
    /// Gets or initializes the layer type.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Gets or initializes the cession percentage.
    /// </summary>
    public decimal Cession { get; init; }

    /// <summary>
    /// Gets or initializes the share percentage.
    /// </summary>
    public decimal Share { get; init; }

    /// <summary>
    /// Gets or initializes the attachment point.
    /// </summary>
    public decimal Attach { get; init; }

    /// <summary>
    /// Gets or initializes the layer limit.
    /// </summary>
    public decimal Limit { get; init; }

    /// <summary>
    /// Gets or initializes the aggregate attachment point.
    /// </summary>
    public decimal AggAttach { get; init; }

    /// <summary>
    /// Gets or initializes the aggregate limit.
    /// </summary>
    public decimal AggLimit { get; init; }

    /// <summary>
    /// Gets or initializes the number of reinstatements.
    /// </summary>
    public int NumReinst { get; init; }

    /// <summary>
    /// Gets or initializes the Estimated Premium Income (EPI).
    /// </summary>
    public decimal EPI { get; init; }

    /// <summary>
    /// Gets or initializes the rate on line.
    /// </summary>
    public decimal Rate { get; init; }

    /// <summary>
    /// Gets or initializes the commission percentage.
    /// </summary>
    public decimal Commission { get; init; }

    /// <summary>
    /// Gets or initializes the brokerage percentage.
    /// </summary>
    public decimal Brokerage { get; init; }

    /// <summary>
    /// Gets or initializes the tax percentage.
    /// </summary>
    public decimal Tax { get; init; }

    /// <summary>
    /// Gets or initializes the reinstatement premium.
    /// </summary>
    public decimal ReinstPrem { get; init; }

    /// <summary>
    /// Gets or initializes the no claims bonus percentage.
    /// </summary>
    public decimal NoClaimsBonus { get; init; }

    /// <summary>
    /// Gets or initializes the profit commission percentage.
    /// </summary>
    public decimal ProfitComm { get; init; }

    /// <summary>
    /// Gets or initializes the management expense percentage.
    /// </summary>
    public decimal MgmtExp { get; init; }

    /// <summary>
    /// Gets or initializes the minimum and deposit premium.
    /// </summary>
    public decimal MDPrem { get; init; }
}

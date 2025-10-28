using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Insurance.Domain;

/// <summary>
/// Represents the reinsurance acceptance with financial terms and coverage sections.
/// </summary>
public record ReinsuranceAcceptance
{
    /// <summary>
    /// Gets or initializes the unique acceptance identifier.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Gets or initializes the pricingId this acceptance belongs to.
    /// </summary>
    public string? PricingId { get; init; }

    /// <summary>
    /// Gets or initializes the acceptance name or description.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or initializes the cession percentage.
    /// </summary>
    public double Cession { get; init; }

    /// <summary>
    /// Gets or initializes the share percentage.
    /// </summary>
    public double Share { get; init; }

    /// <summary>
    /// Gets or initializes the collection of reinsurance sections (layers).
    /// </summary>
    public IReadOnlyCollection<ReinsuranceSection>? Sections { get; init; }

    /// <summary>
    /// Gets or initializes the Estimated Premium Income (EPI).
    /// </summary>
    public double EPI { get; init; }

    /// <summary>
    /// Gets or initializes the rate.
    /// </summary>
    public double Rate { get; init; }

    /// <summary>
    /// Gets or initializes the commission percentage.
    /// </summary>
    public double Commission { get; init; }

    /// <summary>
    /// Gets or initializes the brokerage percentage.
    /// </summary>
    public double Brokerage { get; init; }

    /// <summary>
    /// Gets or initializes the tax percentage.
    /// </summary>
    public double Tax { get; init; }

    /// <summary>
    /// Gets or initializes the reinstatement premium.
    /// </summary>
    public double ReinstPrem { get; init; }

    /// <summary>
    /// Gets or initializes the no claims bonus percentage.
    /// </summary>
    public double NoClaimsBonus { get; init; }

    /// <summary>
    /// Gets or initializes the profit commission percentage.
    /// </summary>
    public double ProfitComm { get; init; }


    /// <summary>
    /// Gets or initializes the minimum and deposit premium.
    /// </summary>
    public double MDPrem { get; init; }
}

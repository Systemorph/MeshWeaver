// <meshweaver>
// Id: FutuReDataCube
// DisplayName: FutuRe Data Cube
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Virtual data cube for FutuRe profitability analysis.
/// One row per Month x LineOfBusiness x AmountType x BusinessUnit.
/// Profit = Premium - Claims - InternalCost - ExternalCost - CapitalCost.
/// </summary>
public record FutuReDataCube
{
    /// <summary>
    /// Composite key: "Month-LoB-AmountType-BU", e.g. "2025-01-PROP-Premium-EuropeRe".
    /// </summary>
    [Key]
    [Display(Name = "Count")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Month in "yyyy-MM" format, e.g. "2025-01".
    /// </summary>
    [Dimension(typeof(string), nameof(Month))]
    [Display(Name = "Month")]
    public string Month { get; init; } = string.Empty;

    /// <summary>
    /// Quarter label, e.g. "Q1-2025".
    /// </summary>
    [Dimension(typeof(string), nameof(Quarter))]
    [Display(Name = "Quarter")]
    public string Quarter { get; init; } = string.Empty;

    /// <summary>
    /// Calendar year.
    /// </summary>
    [NotVisible]
    public int Year { get; init; }

    /// <summary>
    /// Group line of business system name, e.g. "PROP".
    /// </summary>
    [NotVisible]
    public string LineOfBusiness { get; init; } = string.Empty;

    /// <summary>
    /// Group line of business display name, e.g. "Property".
    /// </summary>
    [Dimension(typeof(string), nameof(LineOfBusinessName))]
    [Display(Name = "Line of Business")]
    public string LineOfBusinessName { get; init; } = string.Empty;

    /// <summary>
    /// Local (business unit) line of business system name, e.g. "HOUSEHOLD".
    /// </summary>
    [Dimension(typeof(string), nameof(LocalLineOfBusiness))]
    [Display(Name = "Local LoB")]
    public string LocalLineOfBusiness { get; init; } = string.Empty;

    /// <summary>
    /// Local (business unit) line of business display name, e.g. "Household".
    /// </summary>
    [NotVisible]
    public string LocalLineOfBusinessName { get; init; } = string.Empty;

    /// <summary>
    /// Amount type system name, e.g. "Premium", "Claims".
    /// </summary>
    [Dimension(typeof(string), nameof(AmountType))]
    [Display(Name = "Amount Type")]
    public string AmountType { get; init; } = string.Empty;

    /// <summary>
    /// Business unit identifier, e.g. "EuropeRe", "AmericasIns".
    /// </summary>
    [Dimension(typeof(string), nameof(BusinessUnit))]
    [Display(Name = "Business Unit")]
    public string BusinessUnit { get; init; } = string.Empty;

    /// <summary>
    /// Estimated (budgeted) amount for this month/LoB/AmountType combination.
    /// </summary>
    [Display(Name = "Estimate")]
    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double Estimate { get; init; }

    /// <summary>
    /// Actual (observed) amount. Null for amount types without actuals
    /// (InternalCost, CapitalCost, ExpectedProfit) or future months.
    /// </summary>
    [Display(Name = "Actual")]
    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double? Actual { get; init; }

    /// <summary>
    /// Variance = Actual - Estimate. Positive means favorable for income,
    /// unfavorable for costs.
    /// </summary>
    [Display(Name = "Variance")]
    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double? Variance => Actual.HasValue ? Actual.Value - Estimate : null;
}

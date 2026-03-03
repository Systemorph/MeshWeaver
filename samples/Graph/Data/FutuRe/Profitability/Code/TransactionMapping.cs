// <meshweaver>
// Id: TransactionMapping
// DisplayName: Transaction Mapping
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Maps local (business unit) lines of business to group lines of business
/// with percentage splits. One local LoB can map to multiple group LoBs.
/// Used at data-load time to transform local reporting into group profitability.
/// </summary>
public record TransactionMapping
{
    /// <summary>
    /// Composite key: {BU}-{LocalLoB}-{GroupLoB} (e.g. EUR-HOUSEHOLD-PROP).
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Business unit that writes under the local LoB (e.g. EuropeRe, AmericasIns).
    /// </summary>
    [Dimension(typeof(string), nameof(BusinessUnit))]
    [Display(Name = "Business Unit")]
    public string BusinessUnit { get; init; } = string.Empty;

    /// <summary>
    /// Local line of business code as used by the business unit.
    /// </summary>
    [Dimension(typeof(string), nameof(LocalLineOfBusiness))]
    [Display(Name = "Local LoB")]
    public string LocalLineOfBusiness { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name of the local line of business.
    /// </summary>
    [Display(Name = "Local LoB Name")]
    public string LocalLineOfBusinessName { get; init; } = string.Empty;

    /// <summary>
    /// Group-level line of business code that this local LoB maps to.
    /// </summary>
    [Dimension(typeof(string), nameof(GroupLineOfBusiness))]
    [Display(Name = "Group LoB")]
    public string GroupLineOfBusiness { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable name of the group line of business.
    /// </summary>
    [Display(Name = "Group LoB Name")]
    public string GroupLineOfBusinessName { get; init; } = string.Empty;

    /// <summary>
    /// Fraction of local LoB amount allocated to this group LoB (0..1).
    /// </summary>
    [Display(Name = "Percentage")]
    [DisplayFormat(DataFormatString = "{0:P0}")]
    public double Percentage { get; init; }
}

// <meshweaver>
// Id: BalanceSheetEntry
// DisplayName: Balance Sheet Entry
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// One atomic balance-sheet fact: the value of one Position in one Year.
/// Entries are mesh nodes — there is NO Id property; the node path is the
/// identity. Every dimension column stores the PATH of a dimension node, and
/// each [MeshNode] attribute queries by the dimension TYPE's path, so the
/// Edit form renders pickers over the dimension nodes.
/// </summary>
public record BalanceSheetEntry
{
    /// <summary>Path of the Position node this value belongs to.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    [Display(Name = "Position")]
    public string Position { get; init; } = string.Empty;

    /// <summary>Path of the reporting Year node.</summary>
    [MeshNode("nodeType:PensionFund/Year")]
    [Display(Name = "Year")]
    public string Year { get; init; } = string.Empty;

    /// <summary>Path of the Currency node (the fund reports in CHF).</summary>
    [MeshNode("nodeType:PensionFund/Currency")]
    [Display(Name = "Currency")]
    public string Currency { get; init; } = string.Empty;

    /// <summary>Value in millions of the referenced currency.</summary>
    [DisplayFormat(DataFormatString = "{0:N1}")]
    [Display(Name = "Amount (m)")]
    public double Amount { get; init; }
}

// <meshweaver>
// Id: AmountType
// DisplayName: Amount Type Reference Data
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

/// <summary>
/// Amount type dimension for profitability analysis.
/// Defines the categories of financial amounts in the insurance P&amp;L.
/// Premium - Claims - Internal Cost - External Cost - Capital Cost = Profit.
/// </summary>
public record AmountType : Dimension
{
    /// <summary>
    /// Display order in reports and charts.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Sign convention: +1 for income items (Premium), -1 for cost items.
    /// Used to compute profit: sum of (Amount * Sign) across all types.
    /// </summary>
    public int Sign { get; init; }

    /// <summary>
    /// Whether actual (observed) values are tracked for this amount type.
    /// Only Premium, Claims, and External Cost have actuals; other types
    /// are estimate-only.
    /// </summary>
    public bool HasActuals { get; init; }
}

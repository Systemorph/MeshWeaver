// <meshweaver>
// Id: AmountTypeData
// DisplayName: Amount Type Data
// </meshweaver>

/// <summary>
/// Static reference data for amount types in profitability reporting.
/// </summary>
public static class AmountTypeData
{
    public static readonly AmountType Premium = new()
    {
        SystemName = "Premium",
        DisplayName = "Premium",
        Order = 0,
        Sign = 1,
        HasActuals = true
    };

    public static readonly AmountType Claims = new()
    {
        SystemName = "Claims",
        DisplayName = "Claims",
        Order = 1,
        Sign = -1,
        HasActuals = true
    };

    public static readonly AmountType InternalCost = new()
    {
        SystemName = "InternalCost",
        DisplayName = "Internal Cost",
        Order = 2,
        Sign = -1,
        HasActuals = false
    };

    public static readonly AmountType ExternalCost = new()
    {
        SystemName = "ExternalCost",
        DisplayName = "External Cost",
        Order = 3,
        Sign = -1,
        HasActuals = true
    };

    public static readonly AmountType CapitalCost = new()
    {
        SystemName = "CapitalCost",
        DisplayName = "Capital Cost",
        Order = 4,
        Sign = -1,
        HasActuals = false
    };

    public static readonly AmountType ExpectedProfit = new()
    {
        SystemName = "ExpectedProfit",
        DisplayName = "Expected Profit",
        Order = 5,
        Sign = 1,
        HasActuals = false
    };

    public static readonly AmountType[] All =
    [
        Premium, Claims, InternalCost, ExternalCost, CapitalCost, ExpectedProfit
    ];

    /// <summary>
    /// Looks up an amount type by system name. Returns Premium as fallback.
    /// </summary>
    public static AmountType GetById(string? id) =>
        All.FirstOrDefault(a => a.SystemName == id) ?? Premium;
}

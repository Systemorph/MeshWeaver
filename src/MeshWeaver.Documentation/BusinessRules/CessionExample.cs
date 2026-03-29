namespace MeshWeaver.Documentation.BusinessRules;

// ═══════════════════════════════════════════════════════
// 1. DOMAIN MODEL — Immutable records for cashflows and layers
// ═══════════════════════════════════════════════════════

/// <summary>
/// A single claim cashflow with gross amount.
/// </summary>
public record Cashflow(string ClaimId, string LineOfBusiness, double GrossAmount);

/// <summary>
/// Excess-of-Loss layer: cedes the portion of each claim
/// between AttachmentPoint and AttachmentPoint + Limit.
/// Example: "500k xs 200k" means AttachmentPoint=200k, Limit=500k.
/// </summary>
public record ExcessOfLossLayer(
    string Id,
    string Name,
    double AttachmentPoint,
    double Limit
);

/// <summary>
/// Result of applying a layer to a single cashflow.
/// </summary>
public record CededCashflow(
    string ClaimId,
    string LayerId,
    double GrossAmount,
    double CededAmount,
    double RetainedAmount
);

/// <summary>
/// Summary statistics for a cession result.
/// </summary>
public record CessionSummary(
    int ClaimCount,
    double TotalGross,
    double TotalCeded,
    double TotalRetained,
    double CessionRatio
);

// ═══════════════════════════════════════════════════════
// 2. BUSINESS RULES — Pure functions, no framework dependencies
// ═══════════════════════════════════════════════════════

/// <summary>
/// Reinsurance cession engine. Applies Excess-of-Loss layers to claim cashflows.
/// This is pure domain logic — no MeshWeaver dependency.
/// For the production version with time series, proportional covers, and aggregate layers,
/// see MeshWeaver.Reinsurance/Cession/CededCashflows.cs.
/// </summary>
public static class CessionEngine
{
    /// <summary>
    /// Applies an Excess-of-Loss layer to a set of cashflows.
    /// Per claim: Ceded = min(Limit, max(0, Gross - AttachmentPoint))
    /// </summary>
    public static IReadOnlyList<CededCashflow> CedeIntoLayer(
        IEnumerable<Cashflow> cashflows,
        ExcessOfLossLayer layer)
    {
        return cashflows.Select(cf =>
        {
            var ceded = Math.Min(layer.Limit,
                                 Math.Max(0, cf.GrossAmount - layer.AttachmentPoint));
            return new CededCashflow(
                cf.ClaimId, layer.Id,
                cf.GrossAmount, ceded,
                cf.GrossAmount - ceded);
        }).ToList();
    }

    /// <summary>
    /// Computes summary statistics for ceded cashflows.
    /// </summary>
    public static CessionSummary Summarize(IReadOnlyList<CededCashflow> results)
    {
        var totalGross = results.Sum(r => r.GrossAmount);
        var totalCeded = results.Sum(r => r.CededAmount);
        return new CessionSummary(
            ClaimCount: results.Count,
            TotalGross: totalGross,
            TotalCeded: totalCeded,
            TotalRetained: totalGross - totalCeded,
            CessionRatio: totalGross > 0 ? totalCeded / totalGross : 0
        );
    }
}

// ═══════════════════════════════════════════════════════
// 3. SAMPLE DATA
// ═══════════════════════════════════════════════════════

public static class CessionSampleData
{
    /// <summary>
    /// XL layer: 500k xs 200k (covers claims between 200k and 700k).
    /// </summary>
    public static readonly ExcessOfLossLayer Layer = new(
        Id: "XL1",
        Name: "Motor XL 500k xs 200k",
        AttachmentPoint: 200_000,
        Limit: 500_000
    );

    /// <summary>
    /// Sample claims spanning below, within, and above the layer.
    /// </summary>
    public static readonly Cashflow[] Claims =
    [
        new("C001", "Motor", 150_000),     // Below attachment — fully retained
        new("C002", "Motor", 350_000),     // Partially ceded: 150k
        new("C003", "Motor", 800_000),     // Hits limit: 500k ceded
        new("C004", "Motor", 50_000),      // Below attachment
        new("C005", "Motor", 1_200_000),   // Hits limit: 500k ceded
        new("C006", "Motor", 250_000),     // Partially ceded: 50k
        new("C007", "Motor", 400_000),     // Partially ceded: 200k
        new("C008", "Motor", 180_000),     // Below attachment
        new("C009", "Motor", 700_000),     // Hits limit: 500k ceded
        new("C010", "Motor", 300_000),     // Partially ceded: 100k
    ];
}

using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.BusinessRules;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.Documentation.DataCube;

// ═══════════════════════════════════════════════════════
// 1. THE DIMENSIONS — mesh nodes referenced by PATH
// ═══════════════════════════════════════════════════════

/// <summary>Which side of the balance sheet a position belongs to.</summary>
public enum BalanceSheetSide
{
    Assets,
    Liabilities,
    /// <summary>Computed positions (sums, ratios) — not on either side.</summary>
    Computed,
}

/// <summary>How a position's value is obtained.</summary>
public enum PositionAggregation
{
    /// <summary>Value comes from a BalanceSheetEntry fact node.</summary>
    Atomic,
    /// <summary>Value = Σ Weight·Value(Component) over <see cref="Position.Components"/>.</summary>
    Sum,
    /// <summary>Value = Value(Numerator) / Value(Denominator).</summary>
    Ratio,
}

/// <summary>
/// One weighted operand of a computed (Sum) position. The component references
/// another Position NODE by its mesh path — computed positions are modelled
/// out of other positions, all the way down to the atomic ones.
/// </summary>
public record PositionComponent
{
    /// <summary>Mesh path of the referenced Position node.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    public string Position { get; init; } = string.Empty;

    /// <summary>Weight in the sum: +1 adds, -1 subtracts.</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>
/// A balance-sheet position of a pension fund — the dimension that says what a
/// value MEANS. Positions are mesh nodes: their identity is the node path, so
/// facts and formulas reference them by path; name, description, and display
/// order live on the MESH NODE itself (<c>MeshNode.Description</c>,
/// <c>MeshNode.Order</c>) — the content carries only what the node doesn't
/// already have. Atomic positions take their value from fact entries; computed
/// positions (Total Assets, Balance Sheet Sum, Funding Ratio) define a formula
/// over other positions. The same record ships as a Code node in
/// <c>samples/Graph/Data/PensionFund/Position/Source/Position.cs</c>; the doc
/// page <c>Doc/DataMesh/DataCubes</c> walks through it and
/// <c>PensionFundExampleTest</c> pins every number.
/// </summary>
public record Position
{
    /// <summary>Assets, Liabilities, or Computed.</summary>
    public BalanceSheetSide Side { get; init; }

    /// <summary>Atomic (from facts), Sum (weighted components), or Ratio.</summary>
    public PositionAggregation Aggregation { get; init; } = PositionAggregation.Atomic;

    /// <summary>Weighted operands for <see cref="PositionAggregation.Sum"/> positions.</summary>
    public PositionComponent[]? Components { get; init; }

    /// <summary>Numerator position path for <see cref="PositionAggregation.Ratio"/>.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    public string? Numerator { get; init; }

    /// <summary>Denominator position path for <see cref="PositionAggregation.Ratio"/>.</summary>
    [MeshNode("nodeType:PensionFund/Position")]
    public string? Denominator { get; init; }
}

// ═══════════════════════════════════════════════════════
// 2. THE FACT — no Id: the node path IS the identity
// ═══════════════════════════════════════════════════════

/// <summary>
/// One atomic balance-sheet fact: the value of one Position in one Year.
/// Entries are mesh nodes — there is NO Id property; every dimension column
/// stores the PATH of a dimension node, and the [MeshNode] attribute queries
/// by the dimension TYPE's path, so the Edit form renders node pickers.
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

// ═══════════════════════════════════════════════════════
// 3. THE BUSINESS RULES — scopes over the position cube
// ═══════════════════════════════════════════════════════

/// <summary>
/// The storage every balance-sheet scope computes against: the position
/// dimension nodes (by path) and the atomic fact values (by position × year
/// path pair).
/// </summary>
public record BalanceSheetStorage
{
    /// <summary>Position content by position node path.</summary>
    public required ImmutableDictionary<string, Position> Positions { get; init; }

    /// <summary>Atomic amounts (CHF m) by (position path, year path).</summary>
    public required ImmutableDictionary<(string Position, string Year), double> Amounts { get; init; }

    /// <summary>All year node paths, ascending.</summary>
    public required ImmutableArray<string> Years { get; init; }
}

/// <summary>
/// Identity of a position value: one Position node in one Year node —
/// both referenced by mesh path.
/// </summary>
public record PositionYear(string Position, string Year);

/// <summary>
/// THE business rule of the balance sheet: what a position is worth.
/// Atomic positions read the fact value; Sum positions fold their weighted
/// components (each component being another PositionValue scope — composition
/// all the way down to the atoms); Ratio positions divide two other positions.
/// Scope instances are cached per identity by the ScopeRegistry, so shared
/// sub-positions (e.g. Total Assets inside both Available Assets and the
/// Funding Ratio) are computed exactly once. The same interface ships as a
/// Code node in <c>samples/Graph/Data/PensionFund/BalanceSheet/Source/BalanceSheetScopes.cs</c>.
/// </summary>
public interface PositionValue : IScope<PositionYear, BalanceSheetStorage>
{
    /// <summary>The position's dimension node content.</summary>
    Position Position => GetStorage().Positions[Identity.Position];

    /// <summary>The computed value (CHF m) of this position in this year.</summary>
    double Value => Position.Aggregation switch
    {
        PositionAggregation.Atomic =>
            GetStorage().Amounts.TryGetValue((Identity.Position, Identity.Year), out var v) ? v : 0,

        PositionAggregation.Sum =>
            (Position.Components ?? [])
                .Sum(c => c.Weight * GetScope<PositionValue>(new PositionYear(c.Position, Identity.Year)).Value),

        PositionAggregation.Ratio =>
            GetScope<PositionValue>(new PositionYear(Position.Denominator!, Identity.Year)).Value is var denominator
            && denominator != 0
                ? GetScope<PositionValue>(new PositionYear(Position.Numerator!, Identity.Year)).Value / denominator
                : 0,

        _ => 0,
    };
}

/// <summary>
/// Year-level summary scope: composes the headline figures of one reporting
/// year out of the position scopes. The position paths are resolved from the
/// storage by name suffix, so the scope works for any position set.
/// </summary>
public interface BalanceSheetSummary : IScope<string, BalanceSheetStorage>
{
    private static string PathOf(BalanceSheetStorage storage, string suffix)
        => storage.Positions.Keys.First(p => p.EndsWith("/" + suffix));

    double BalanceSheetSum => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "BalanceSheetSum"), Identity)).Value;

    double TotalAssets => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "TotalAssets"), Identity)).Value;

    double TotalLiabilities => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "TotalLiabilities"), Identity)).Value;

    double PensionCapital => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "PensionCapital"), Identity)).Value;

    double FundingRatio => GetScope<PositionValue>(
        new PositionYear(PathOf(GetStorage(), "FundingRatio"), Identity)).Value;

    /// <summary>The balance check: assets and liabilities must match.</summary>
    bool Balances => Math.Abs(TotalAssets - TotalLiabilities) < 1e-9;
}

// ═══════════════════════════════════════════════════════
// 4. SAMPLE DATA — the Helvetia Vorsorge balance sheet
// ═══════════════════════════════════════════════════════

/// <summary>
/// The deterministic balance sheet of the fictional Swiss pension fund
/// "Helvetia Vorsorge", identical to the instance nodes shipped in
/// <c>samples/Graph/Data/PensionFund/</c> (CHF m). Both years balance by
/// construction: 2024 assets = liabilities = 1,060.0 · 2025 = 1,142.0.
/// Funding ratio 2024 = 1,010 / 920 ≈ 109.8% · 2025 = 1,084 / 964 ≈ 112.4%.
/// </summary>
public static class PensionFundSampleData
{
    private const string Ns = "PensionFund/Position";

    private static PositionComponent[] Cmp(params (string Position, double Weight)[] components)
        => components.Select(c => new PositionComponent { Position = $"{Ns}/{c.Position}", Weight = c.Weight }).ToArray();

    private static readonly string[] AssetAtoms =
        ["Cash", "Bonds", "Equities", "RealEstate", "Alternatives", "Receivables"];

    private static readonly string[] LiabilityAtoms =
    [
        "Payables", "AccruedLiabilities", "EmployerContributionReserve", "NonTechnicalProvisions",
        "ActiveMembersCapital", "PensionersCapital", "TechnicalProvisions", "ValueFluctuationReserve", "FreeFunds",
    ];

    /// <summary>
    /// All 21 position nodes by path: 15 atomic + 6 computed. Display order is
    /// NOT in the content — it lives on the mesh node (<c>MeshNode.Order</c>);
    /// see <see cref="OrderedPositions"/> for the node-order sequence.
    /// </summary>
    public static readonly ImmutableDictionary<string, Position> Positions =
        AssetAtoms.Select(id => (Path: $"{Ns}/{id}",
                Position: new Position { Side = BalanceSheetSide.Assets, Aggregation = PositionAggregation.Atomic }))
            .Concat(LiabilityAtoms.Select(id => (Path: $"{Ns}/{id}",
                Position: new Position { Side = BalanceSheetSide.Liabilities, Aggregation = PositionAggregation.Atomic })))
            .Append(($"{Ns}/TotalAssets", new Position
            {
                Side = BalanceSheetSide.Computed, Aggregation = PositionAggregation.Sum,
                Components = Cmp(AssetAtoms.Select(a => (a, 1.0)).ToArray()),
            }))
            .Append(($"{Ns}/TotalLiabilities", new Position
            {
                Side = BalanceSheetSide.Computed, Aggregation = PositionAggregation.Sum,
                Components = Cmp(LiabilityAtoms.Select(l => (l, 1.0)).ToArray()),
            }))
            .Append(($"{Ns}/BalanceSheetSum", new Position
            {
                Side = BalanceSheetSide.Computed, Aggregation = PositionAggregation.Sum,
                Components = Cmp(AssetAtoms.Select(a => (a, 1.0)).ToArray()),
            }))
            .Append(($"{Ns}/PensionCapital", new Position
            {
                Side = BalanceSheetSide.Computed, Aggregation = PositionAggregation.Sum,
                Components = Cmp(("ActiveMembersCapital", 1), ("PensionersCapital", 1), ("TechnicalProvisions", 1)),
            }))
            .Append(($"{Ns}/AvailableAssets", new Position
            {
                Side = BalanceSheetSide.Computed, Aggregation = PositionAggregation.Sum,
                Components = Cmp(("TotalAssets", 1), ("Payables", -1), ("AccruedLiabilities", -1),
                    ("EmployerContributionReserve", -1), ("NonTechnicalProvisions", -1)),
            }))
            .Append(($"{Ns}/FundingRatio", new Position
            {
                Side = BalanceSheetSide.Computed, Aggregation = PositionAggregation.Ratio,
                Numerator = $"{Ns}/AvailableAssets", Denominator = $"{Ns}/PensionCapital",
            }))
            .ToImmutableDictionary(x => x.Item1, x => x.Item2);

    /// <summary>
    /// Position paths in display order, mirroring the node-level
    /// <c>MeshNode.Order</c> values of the sample nodes (assets 1-6,
    /// liabilities 11-19, computed 21-26).
    /// </summary>
    public static readonly ImmutableList<string> OrderedPositions =
        AssetAtoms.Concat(LiabilityAtoms)
            .Concat(["TotalAssets", "TotalLiabilities", "BalanceSheetSum", "PensionCapital", "AvailableAssets", "FundingRatio"])
            .Select(id => $"{Ns}/{id}")
            .ToImmutableList();

    /// <summary>The two reporting-year node paths.</summary>
    public static readonly ImmutableArray<string> Years =
        ["PensionFund/Year/2024", "PensionFund/Year/2025"];

    /// <summary>The 30 atomic fact values (CHF m), identical to the sample entry nodes.</summary>
    public static readonly ImmutableDictionary<(string Position, string Year), double> Amounts =
        new Dictionary<string, (double Y2024, double Y2025)>
        {
            ["Cash"] = (50, 60), ["Bonds"] = (400, 410), ["Equities"] = (300, 340),
            ["RealEstate"] = (200, 210), ["Alternatives"] = (100, 110), ["Receivables"] = (10, 12),
            ["Payables"] = (15, 18), ["AccruedLiabilities"] = (5, 6),
            ["EmployerContributionReserve"] = (20, 22), ["NonTechnicalProvisions"] = (10, 12),
            ["ActiveMembersCapital"] = (600, 620), ["PensionersCapital"] = (280, 300),
            ["TechnicalProvisions"] = (40, 44), ["ValueFluctuationReserve"] = (80, 110), ["FreeFunds"] = (10, 10),
        }
        .SelectMany(kvp => new[]
        {
            (Key: ($"{Ns}/{kvp.Key}", Years[0]), Amount: kvp.Value.Y2024),
            (Key: ($"{Ns}/{kvp.Key}", Years[1]), Amount: kvp.Value.Y2025),
        })
        .ToImmutableDictionary(x => x.Key, x => x.Amount);

    /// <summary>The fully populated storage the scopes evaluate against.</summary>
    public static BalanceSheetStorage Storage => new()
    {
        Positions = Positions,
        Amounts = Amounts,
        Years = Years,
    };

    /// <summary>The 30 fact entries as records — the cube the pivot/chart demos slice.</summary>
    public static readonly IReadOnlyList<BalanceSheetEntry> Entries =
        Amounts.Select(kvp => new BalanceSheetEntry
        {
            Position = kvp.Key.Position,
            Year = kvp.Key.Year,
            Currency = "PensionFund/Currency/CHF",
            Amount = kvp.Value,
        }).ToList();
}

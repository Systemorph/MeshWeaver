using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Documentation.DataCube;

// ═══════════════════════════════════════════════════════
// 1. THE FACT — one row per LineOfBusiness × Year × Currency
// ═══════════════════════════════════════════════════════

/// <summary>
/// A cube fact: a local-currency amount keyed by three dimensions.
/// The [Dimension] attributes make the columns groupable in the pivot
/// builder and render as dropdowns on the Edit form. The same record
/// ships as a Code node in the FutuRe sample
/// (samples/Graph/Data/FutuRe/FxCube/Source/FxCube.cs) — the doc page
/// Doc/DataMesh/DataCubes renders it live, and
/// <c>FxCubeExampleTest</c> pins the numbers both display.
/// </summary>
public record FxCubeFact
{
    /// <summary>Composite key "LoB-Year-Currency", e.g. "Property-2024-EUR".</summary>
    [Key]
    public required string Id { get; init; }

    [Dimension(typeof(string), nameof(LineOfBusiness))]
    public required string LineOfBusiness { get; init; }

    [Dimension(typeof(string), nameof(Year))]
    public required string Year { get; init; }

    [Dimension(typeof(string), nameof(Currency))]
    public required string Currency { get; init; }

    /// <summary>Amount in the row's local currency.</summary>
    public required double Amount { get; init; }
}

// ═══════════════════════════════════════════════════════
// 2. FX CONVERSION + SLICE & DICE — pure functions
// ═══════════════════════════════════════════════════════

/// <summary>Which rate set converts local amounts to the group currency (CHF).</summary>
public enum FxMode
{
    /// <summary>Budget rates.</summary>
    Plan,
    /// <summary>Market rates.</summary>
    Actual,
}

/// <summary>
/// The cube verbs as pure functions — no framework dependency:
/// <b>convert</b> local amounts to the group currency via the rate table,
/// <b>slice</b> (group one dimension, total a measure),
/// <b>dice</b> (fix dimension members to get a sub-cube).
/// The UI equivalents (<c>ToPivotGrid</c>, <c>SliceBy(...).ToColumnChart</c>)
/// run the same grouping inside the controls.
/// </summary>
public static class FxCube
{
    /// <summary>Converts every fact's Amount to CHF using the mode's rate table.</summary>
    public static IReadOnlyList<FxCubeFact> ConvertToGroupCurrency(
        IEnumerable<FxCubeFact> facts,
        IReadOnlyDictionary<string, (double Plan, double Actual)> rates,
        FxMode mode)
        => facts.Select(f =>
            {
                var (plan, actual) = rates[f.Currency];
                var rate = mode == FxMode.Plan ? plan : actual;
                return f with { Amount = f.Amount * rate, Currency = "CHF" };
            })
            .ToList();

    /// <summary>Slice: totals of <paramref name="measure"/> per member of one dimension.</summary>
    public static IReadOnlyDictionary<string, double> Slice(
        IEnumerable<FxCubeFact> facts,
        Func<FxCubeFact, string> dimension,
        Func<FxCubeFact, double>? measure = null)
        => facts.GroupBy(dimension)
            .ToDictionary(g => g.Key, g => g.Sum(measure ?? (f => f.Amount)));

    /// <summary>Dice: the sub-cube where every supplied dimension member matches.</summary>
    public static IReadOnlyList<FxCubeFact> Dice(
        IEnumerable<FxCubeFact> facts,
        string? lineOfBusiness = null,
        string? year = null,
        string? currency = null)
        => facts.Where(f =>
                (lineOfBusiness is null || f.LineOfBusiness == lineOfBusiness)
                && (year is null || f.Year == year)
                && (currency is null || f.Currency == currency))
            .ToList();

    /// <summary>Grand total of a measure over the (sub-)cube.</summary>
    public static double GrandTotal(IEnumerable<FxCubeFact> facts)
        => facts.Sum(f => f.Amount);
}

// ═══════════════════════════════════════════════════════
// 3. SAMPLE DATA — deterministic, round numbers
// ═══════════════════════════════════════════════════════

/// <summary>
/// 18 facts = 3 lines of business × 2 years × 3 currencies, with rate tables
/// for plan/actual conversion. Every slice has a clean, assertable answer:
/// local grand total 2,880,000 · plan-CHF 2,690,000 · actual-CHF 2,642,400 ·
/// plan-CHF by year 1,288,000 / 1,402,000 ·
/// plan-CHF by LoB 1,177,000 / 924,500 / 588,500.
/// </summary>
public static class FxCubeSampleData
{
    /// <summary>currency → (plan rate, actual rate) into CHF.</summary>
    public static readonly IReadOnlyDictionary<string, (double Plan, double Actual)> RatesToChf =
        new Dictionary<string, (double, double)>
        {
            ["CHF"] = (1.00, 1.00),
            ["EUR"] = (0.95, 0.93),
            ["USD"] = (0.90, 0.88),
        };

    public static readonly string[] LinesOfBusiness = ["Property", "Casualty", "Specialty"];
    public static readonly string[] Years = ["2024", "2025"];
    public static readonly string[] Currencies = ["CHF", "EUR", "USD"];

    /// <summary>(LoB, Year, Currency) → local amount.</summary>
    private static readonly Dictionary<(string Lob, string Year, string Ccy), double> Amounts = new()
    {
        [("Property", "2024", "CHF")] = 100_000, [("Property", "2025", "CHF")] = 120_000,
        [("Property", "2024", "EUR")] = 200_000, [("Property", "2025", "EUR")] = 220_000,
        [("Property", "2024", "USD")] = 300_000, [("Property", "2025", "USD")] = 320_000,
        [("Casualty", "2024", "CHF")] = 80_000,  [("Casualty", "2025", "CHF")] = 90_000,
        [("Casualty", "2024", "EUR")] = 160_000, [("Casualty", "2025", "EUR")] = 170_000,
        [("Casualty", "2024", "USD")] = 240_000, [("Casualty", "2025", "USD")] = 250_000,
        [("Specialty", "2024", "CHF")] = 50_000, [("Specialty", "2025", "CHF")] = 60_000,
        [("Specialty", "2024", "EUR")] = 100_000, [("Specialty", "2025", "EUR")] = 110_000,
        [("Specialty", "2024", "USD")] = 150_000, [("Specialty", "2025", "USD")] = 160_000,
    };

    public static readonly IReadOnlyList<FxCubeFact> Facts =
        Amounts.Select(kvp => new FxCubeFact
        {
            Id = $"{kvp.Key.Lob}-{kvp.Key.Year}-{kvp.Key.Ccy}",
            LineOfBusiness = kvp.Key.Lob,
            Year = kvp.Key.Year,
            Currency = kvp.Key.Ccy,
            Amount = kvp.Value,
        }).ToList();
}

// <meshweaver>
// Id: FxCube
// DisplayName: FX Cube
// </meshweaver>

using System.ComponentModel.DataAnnotations;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

/// <summary>
/// A minimal teaching cube: one fact row per LineOfBusiness × Year × Currency,
/// carrying a local-currency Amount. FX conversion turns local amounts into the
/// group currency (CHF) at plan or actual rates — the same idea the full
/// FutuRe profitability cube (GroupAnalysis) applies at scale.
/// The documentation page Doc/DataMesh/DataCubes walks through this node, and
/// MeshWeaver.Documentation.Test/FxCubeExampleTest pins all the numbers below.
/// </summary>
public record FxCubeFact
{
    /// <summary>Composite key "LoB-Year-Currency", e.g. "Property-2024-EUR".</summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>Line of business — see the FutuRe/LineOfBusiness dimension nodes.</summary>
    [Dimension(typeof(string), nameof(LineOfBusiness))]
    [Display(Name = "Line of Business")]
    public string LineOfBusiness { get; init; } = string.Empty;

    [Dimension(typeof(string), nameof(Year))]
    public string Year { get; init; } = string.Empty;

    /// <summary>ISO currency code of the local amount.</summary>
    [Dimension(typeof(string), nameof(Currency))]
    public string Currency { get; init; } = string.Empty;

    /// <summary>Amount in the row's local currency.</summary>
    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double Amount { get; init; }
}

/// <summary>Which rate set converts local amounts into CHF.</summary>
public enum FxMode
{
    Plan,
    Actual,
}

/// <summary>
/// The cube verbs as pure functions: convert (FX), slice (group + total),
/// dice (fix members → sub-cube). The pivot grid and the SliceBy chart
/// builders run the same grouping inside the controls.
/// </summary>
public static class FxCubeEngine
{
    /// <summary>Converts every fact's Amount into CHF using the mode's rate.</summary>
    public static IReadOnlyList<FxCubeFact> ConvertToGroupCurrency(
        IEnumerable<FxCubeFact> facts, FxMode mode)
        => facts.Select(f => f with
            {
                Amount = f.Amount * FxCubeData.RateToChf(f.Currency, mode),
                Currency = "CHF",
            })
            .ToList();

    /// <summary>Slice: totals per member of one dimension.</summary>
    public static IReadOnlyDictionary<string, double> Slice(
        IEnumerable<FxCubeFact> facts, Func<FxCubeFact, string> dimension)
        => facts.GroupBy(dimension).ToDictionary(g => g.Key, g => g.Sum(f => f.Amount));

    /// <summary>Dice: the sub-cube where every supplied dimension member matches.</summary>
    public static IReadOnlyList<FxCubeFact> Dice(
        IEnumerable<FxCubeFact> facts,
        string? lineOfBusiness = null, string? year = null, string? currency = null)
        => facts.Where(f =>
                (lineOfBusiness == null || f.LineOfBusiness == lineOfBusiness)
                && (year == null || f.Year == year)
                && (currency == null || f.Currency == currency))
            .ToList();
}

/// <summary>
/// 18 deterministic facts (3 LoBs × 2 years × 3 currencies) and the rate
/// tables. Round numbers so every slice is assertable:
/// local total 2,880,000 · plan-CHF 2,690,000 · actual-CHF 2,642,400.
/// </summary>
public static class FxCubeData
{
    public static double RateToChf(string currency, FxMode mode)
        => (currency, mode) switch
        {
            ("CHF", _) => 1.00,
            ("EUR", FxMode.Plan) => 0.95,
            ("EUR", FxMode.Actual) => 0.93,
            ("USD", FxMode.Plan) => 0.90,
            ("USD", FxMode.Actual) => 0.88,
            _ => 1.00,
        };

    public static readonly FxCubeFact[] Facts = BuildFacts();

    private static FxCubeFact[] BuildFacts()
    {
        // (LoB, Year, Currency, local amount)
        (string Lob, string Year, string Ccy, double Amount)[] rows =
        [
            ("Property", "2024", "CHF", 100_000), ("Property", "2025", "CHF", 120_000),
            ("Property", "2024", "EUR", 200_000), ("Property", "2025", "EUR", 220_000),
            ("Property", "2024", "USD", 300_000), ("Property", "2025", "USD", 320_000),
            ("Casualty", "2024", "CHF",  80_000), ("Casualty", "2025", "CHF",  90_000),
            ("Casualty", "2024", "EUR", 160_000), ("Casualty", "2025", "EUR", 170_000),
            ("Casualty", "2024", "USD", 240_000), ("Casualty", "2025", "USD", 250_000),
            ("Specialty", "2024", "CHF", 50_000), ("Specialty", "2025", "CHF", 60_000),
            ("Specialty", "2024", "EUR", 100_000), ("Specialty", "2025", "EUR", 110_000),
            ("Specialty", "2024", "USD", 150_000), ("Specialty", "2025", "USD", 160_000),
        ];

        return rows.Select(r => new FxCubeFact
        {
            Id = $"{r.Lob}-{r.Year}-{r.Ccy}",
            LineOfBusiness = r.Lob,
            Year = r.Year,
            Currency = r.Ccy,
            Amount = r.Amount,
        }).ToArray();
    }
}

/// <summary>
/// Hub configuration for the FxCube node: the facts as a typed data source
/// plus the cube's layout areas. Referenced from FxCube.json.
/// </summary>
public static class FxCubeConfig
{
    public static MessageHubConfiguration ConfigureFxCube(this MessageHubConfiguration config)
        => config
            .AddData(data => data
                .AddSource(source => source
                    .WithType<FxCubeFact>(t => t.WithInitialData(FxCubeData.Facts))))
            .AddDefaultLayoutAreas()
            .AddLayout(layout => layout.AddFxCubeLayoutAreas());
}

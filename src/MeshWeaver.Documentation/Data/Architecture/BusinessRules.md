---
Name: Business Rules & Calculations
Category: Architecture
Description: Building domain logic with pure calculation engines, reactive layout areas, and interactive charts — illustrated with a reinsurance cession example
Icon: /static/DocContent/Architecture/BusinessRules/icon.svg
---

MeshWeaver lets you wire domain-specific business logic directly into reactive, chart-driven UI without any special framework scaffolding. Your calculation engine stays as plain C#; MeshWeaver handles the subscription plumbing and re-rendering.

This guide walks through a **reinsurance Excess-of-Loss cession** end to end. The same three-layer pattern — domain model, pure engine, reactive layout area — applies to any domain.

The complete working example lives at the @Cession node type, including source files, sample data, and a chart layout area:

@@Cession/MotorXL

For the full production implementation see the MeshWeaver.Reinsurance repository.

---

## The Three-Layer Pattern

Every business rules module in MeshWeaver follows the same structure:

| Layer | What it contains | Key trait |
|---|---|---|
| **Domain Model** | Immutable records for cashflows, contracts, results | Safe to use in reactive pipelines |
| **Business Rules** | Pure C# functions with no side effects | Easy to unit-test in isolation |
| **Layout Areas** | `IObservable<UiControl>` views bound to workspace streams | Re-render automatically when data changes |

The layers are intentionally decoupled. The calculation engine has zero framework imports; the layout area knows nothing about persistence.
<svg viewBox="0 0 760 260" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="20" y="80" width="180" height="100" rx="10" fill="#1565c0" stroke="none"/>
  <text x="110" y="118" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Domain Model</text>
  <text x="110" y="140" text-anchor="middle" fill="#bbdefb" font-size="11">Immutable records</text>
  <text x="110" y="156" text-anchor="middle" fill="#bbdefb" font-size="11">Cashflow · Layer · Result</text>
  <rect x="290" y="80" width="180" height="100" rx="10" fill="#2e7d32" stroke="none"/>
  <text x="380" y="118" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Business Rules</text>
  <text x="380" y="140" text-anchor="middle" fill="#c8e6c9" font-size="11">Pure static functions</text>
  <text x="380" y="156" text-anchor="middle" fill="#c8e6c9" font-size="11">CessionEngine.CedeIntoLayer</text>
  <rect x="560" y="80" width="180" height="100" rx="10" fill="#6a1b9a" stroke="none"/>
  <text x="650" y="118" text-anchor="middle" fill="#fff" font-weight="bold" font-size="14">Layout Area</text>
  <text x="650" y="140" text-anchor="middle" fill="#e1bee7" font-size="11">IObservable&lt;UiControl&gt;</text>
  <text x="650" y="156" text-anchor="middle" fill="#e1bee7" font-size="11">CombineLatest → Chart</text>
  <line x1="200" y1="130" x2="288" y2="130" stroke="#90a4ae" stroke-width="2" marker-end="url(#arrow)"/>
  <line x1="470" y1="130" x2="558" y2="130" stroke="#90a4ae" stroke-width="2" marker-end="url(#arrow)"/>
  <text x="244" y="120" text-anchor="middle" fill="#90a4ae" font-size="11">typed data</text>
  <text x="514" y="120" text-anchor="middle" fill="#90a4ae" font-size="11">results</text>
  <rect x="120" y="210" width="520" height="36" rx="8" fill="none" stroke="#37474f" stroke-width="1.5" stroke-dasharray="5,3"/>
  <text x="380" y="232" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="12">Workspace streams feed typed data; reactive subscriptions propagate changes end-to-end</text>
</svg>

*Three-layer pattern: immutable domain model feeds a pure calculation engine whose results are consumed by a reactive layout area.*

---

## 1. Domain Model

Cashflows and layers are simple records. Immutability makes them safe to pass through reactive pipelines without defensive copies.

```csharp
/// <summary>
/// A single claim cashflow with a gross amount.
/// </summary>
public record Cashflow(string ClaimId, string LineOfBusiness, double GrossAmount);

/// <summary>
/// Excess-of-Loss layer: cedes the portion of each claim between
/// AttachmentPoint and AttachmentPoint + Limit.
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
```

---

## 2. Business Rules — Cession into Layer

The core engine is a pure static class. No framework dependencies, no I/O — just math. This makes it trivially testable with standard xUnit assertions.

The formula per claim is: `Ceded = min(Limit, max(0, Gross − AttachmentPoint))`

```csharp
public static class CessionEngine
{
    /// <summary>
    /// Applies an Excess-of-Loss layer to a set of cashflows.
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
    /// Computes summary statistics for a set of ceded cashflows.
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

public record CessionSummary(
    int ClaimCount,
    double TotalGross,
    double TotalCeded,
    double TotalRetained,
    double CessionRatio
);
```

---

## 3. Layout Area — Ceded Distribution Chart

Layout areas subscribe to workspace data and re-render whenever the underlying streams emit. The layout function returns `IObservable<UiControl>` — MeshWeaver drives the lifecycle.

```csharp
using MeshWeaver.Charting;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

public static class CessionResultsArea
{
    public static LayoutDefinition AddCessionResults(this LayoutDefinition layout)
        => layout.WithView(nameof(CessionResults), CessionResults);

    public static IObservable<UiControl> CessionResults(
        LayoutAreaHost host, RenderingContext ctx)
    {
        return host.Workspace.GetStream<Cashflow>()
            .CombineLatest(
                host.Workspace.GetStream<ExcessOfLossLayer>(),
                (cashflows, layers) =>
                {
                    var layer = layers?.FirstOrDefault();
                    if (cashflows == null || layer == null)
                        return Controls.Markdown("Loading...");

                    var results = CessionEngine.CedeIntoLayer(cashflows, layer);
                    var summary = CessionEngine.Summarize(results);
                    return BuildView(host, results, summary, layer);
                });
    }

    private static UiControl BuildView(
        LayoutAreaHost host,
        IReadOnlyList<CededCashflow> results,
        CessionSummary summary,
        ExcessOfLossLayer layer)
    {
        // Summary statistics card
        var stats = Controls.Markdown(
            $"**Layer:** {layer.Name} (xs {layer.AttachmentPoint:N0} / {layer.Limit:N0})  \n" +
            $"**Claims:** {summary.ClaimCount} | " +
            $"**Gross:** {summary.TotalGross:N0} | " +
            $"**Ceded:** {summary.TotalCeded:N0} | " +
            $"**Retained:** {summary.TotalRetained:N0} | " +
            $"**Ratio:** {summary.CessionRatio:P1}");

        // Histogram of ceded amounts
        var ceded = results.Select(r => r.CededAmount).OrderBy(x => x).ToArray();
        var nonZero = ceded.Where(x => x > 0).ToArray();

        if (nonZero.Length == 0)
            return Controls.Stack
                .WithView(stats)
                .WithView(Controls.Markdown("No claims penetrate this layer."));

        var nBins = Math.Min(50, nonZero.Length);
        var min = nonZero.Min();
        var max = nonZero.Max();
        var binWidth = (max - min) / nBins;
        var histogram = new double[nBins];
        foreach (var v in nonZero)
        {
            var bin = Math.Min((int)((v - min) / binWidth), nBins - 1);
            histogram[bin]++;
        }
        var labels = Enumerable.Range(0, nBins)
            .Select(i => (min + (i + 0.5) * binWidth).ToString("N0"));

        var chart = Chart.Create(DataSet.Bar(histogram))
            .WithLabels(labels)
            .WithTitle($"Ceded Distribution — {layer.Name}")
            .ToControl()
            .WithStyle("width: 100%; height: 400px;");

        return Controls.Stack
            .WithView(Controls.Title("Cession Results", 3))
            .WithView(stats)
            .WithView(chart);
    }
}
```

> **Reactive re-rendering.** `CombineLatest` means the chart rebuilds automatically any time cashflows or the layer definition changes — a filter toolbar just needs to update the workspace stream and the chart follows.

---

## 4. Wire It Together

Register domain types, initial data, and the layout area in the hub configuration:

```csharp
public static MessageHubConfiguration AddReinsuranceExample(
    this MessageHubConfiguration config)
{
    return config
        .AddData(data => data
            .AddSource(source => source
                .WithType<Cashflow>(t => t.WithInitialData(SampleData.Claims))
                .WithType<ExcessOfLossLayer>(t => t.WithInitialData([SampleData.Layer]))
            ))
        .AddLayout(layout => layout
            .WithDefaultArea(nameof(CessionResultsArea.CessionResults))
            .AddCessionResults()
        );
}
```

---

## 5. Sample Data

Ten motor claims spanning all three cession outcomes — below attachment, partial, and full-limit hits:

```csharp
public static class SampleData
{
    public static readonly ExcessOfLossLayer Layer = new(
        Id: "XL1",
        Name: "Motor XL 500k xs 200k",
        AttachmentPoint: 200_000,
        Limit: 500_000
    );

    public static readonly Cashflow[] Claims =
    [
        new("C001", "Motor", 150_000),   // Below attachment — fully retained
        new("C002", "Motor", 350_000),   // Partially ceded: 150k ceded
        new("C003", "Motor", 800_000),   // Hits limit: 500k ceded
        new("C004", "Motor", 50_000),    // Below attachment
        new("C005", "Motor", 1_200_000), // Hits limit: 500k ceded
        new("C006", "Motor", 250_000),   // Partially ceded: 50k
        new("C007", "Motor", 400_000),   // Partially ceded: 200k
        new("C008", "Motor", 180_000),   // Below attachment
        new("C009", "Motor", 700_000),   // Hits limit: 500k ceded
        new("C010", "Motor", 300_000),   // Partially ceded: 100k
    ];
}
```

---

## Live Demo — Cession Calculation

The interactive cell below runs the same `CessionEngine` logic inline. It shows how a Motor XL 500k xs 200k layer splits ten claims into ceded, retained, and below-attachment buckets.

```csharp --render CessionDemo --show-code
var attachmentPoint = 200_000.0;
var limit = 500_000.0;

var claims = new[]
{
    ("C001", 150_000.0),
    ("C002", 350_000.0),
    ("C003", 800_000.0),
    ("C004",  50_000.0),
    ("C005", 1_200_000.0),
    ("C006", 250_000.0),
    ("C007", 400_000.0),
    ("C008", 180_000.0),
    ("C009", 700_000.0),
    ("C010", 300_000.0),
};

var rows = claims.Select(c =>
{
    var ceded   = Math.Min(limit, Math.Max(0, c.Item2 - attachmentPoint));
    var retained = c.Item2 - ceded;
    var outcome = ceded == 0   ? "Below attachment"
                : ceded == limit ? "Full limit"
                :                  "Partial";
    return $"| {c.Item1} | {c.Item2,12:N0} | {ceded,10:N0} | {retained,12:N0} | {outcome} |";
});

var totalGross    = claims.Sum(c => c.Item2);
var totalCeded    = claims.Sum(c => Math.Min(limit, Math.Max(0, c.Item2 - attachmentPoint)));
var totalRetained = totalGross - totalCeded;
var ratio         = totalCeded / totalGross;

var header = "| Claim | Gross | Ceded | Retained | Outcome |\n|---|---:|---:|---:|---|";
var footer = $"\n**Totals — Gross: {totalGross:N0} | Ceded: {totalCeded:N0} | Retained: {totalRetained:N0} | Ratio: {ratio:P1}**";

MeshWeaver.Layout.Controls.Markdown(
    $"### Motor XL 500k xs 200k\n\n{header}\n{string.Join("\n", rows)}\n{footer}"
)
```

---

## Pattern Reference

| Pattern | When to use |
|---|---|
| **Immutable records** | All domain types — safe for reactive pipelines |
| **Pure static functions** | Business logic with no side effects — trivial to unit-test |
| **`IObservable<UiControl>`** | Layout areas that re-render when data changes |
| **`Chart.Create(DataSet.Bar(...))`** | Histograms, scatter plots, line charts |
| **`host.Workspace.GetStream<T>()`** | Subscribe to typed data collections in the workspace |
| **`.CombineLatest()`** | Merge multiple streams into a single computed view |

---

## Further Reading

For the full production implementation — Monte Carlo simulation, time series, proportional/non-proportional covers, and aggregate layers:

- `src/MeshWeaver.Reinsurance/Cession/CededCashflows.cs` — full cession engine with proportional, non-proportional, and aggregate covers
- `src/MeshWeaver.Reinsurance.Pricing/LayoutAreas/DistributionLayoutArea.cs` — PDF/CDF charts with filter toolbars

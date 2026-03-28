---
Name: Business Rules & Calculations
Category: Architecture
Description: Implementing domain logic with data import, cession calculations, and interactive chart layout areas
Icon: /static/DocContent/Architecture/BusinessRules/icon.svg
---

MeshWeaver supports building domain-specific business logic with data import, calculation engines, and interactive chart layout areas. This guide walks through a **reinsurance cession example** — the same pattern applies to any domain.

The complete example is the @Cession node type with source files, sample data, and a layout area with charts:

@@Cession/MotorXL

For the full production implementation, see the MeshWeaver.Reinsurance repository.

# Overview

The pattern has three layers:

1. **Domain Model** — immutable records for cashflows, contracts, and results
2. **Business Rules** — pure C# operations (cession into layer, proportional split)
3. **Layout Areas** — reactive charts with filters bound to the calculation output

# Example: Reinsurance Excess-of-Loss Cession

## 1. Domain Model

Cashflows are simple records with an amount. A reinsurance layer defines the attachment point and limit:

```csharp
/// <summary>
/// A single claim cashflow with gross amount.
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
/// Result of applying a layer to a cashflow.
/// </summary>
public record CededCashflow(
    string ClaimId,
    string LayerId,
    double GrossAmount,
    double CededAmount,
    double RetainedAmount
);
```

## 2. Business Rules — Cession into Layer

The core operation is a pure function. No framework dependencies:

```csharp
public static class CessionEngine
{
    /// <summary>
    /// Applies an Excess-of-Loss layer to a set of cashflows.
    /// Formula per claim: Ceded = min(Limit, max(0, Gross - AttachmentPoint))
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

## 3. Layout Area — Ceded Distribution Chart

Layout areas are reactive: they subscribe to workspace data and re-render when filters change.

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

## 4. Wire It Together

Register domain types, data, and layout in the hub configuration:

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

## 5. Sample Data

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

# Key Patterns

| Pattern | When to Use |
|---------|-------------|
| **Immutable Records** | All domain types — enables safe reactive pipelines |
| **Pure Functions** | Business logic with no side effects — easy to test |
| **`IObservable<UiControl>`** | Layout areas re-render when data changes |
| **`Chart.Create(DataSet.Bar(...))`** | Histograms, scatter plots, line charts |
| **`host.Workspace.GetStream<T>()`** | Subscribe to typed data collections |
| **`.CombineLatest()`** | Merge multiple data streams for computed views |

For the full production implementation with Monte Carlo simulation, time series, proportional/non-proportional covers, and aggregate layers, see:
- `src/MeshWeaver.Reinsurance/Cession/CededCashflows.cs` — cession calculation engine with proportional, non-proportional, and aggregate covers
- `src/MeshWeaver.Reinsurance.Pricing/LayoutAreas/DistributionLayoutArea.cs` — PDF/CDF charts with filter toolbars

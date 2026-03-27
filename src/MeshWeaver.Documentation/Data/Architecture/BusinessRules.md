---
Name: Business Rules & Calculations
Category: Architecture
Description: Implementing domain logic with data import, business rules, and interactive chart layout areas
Icon: Calculator
---

MeshWeaver supports building domain-specific business logic with data import from CSV/Excel, calculation engines, and interactive chart layout areas. This guide walks through a reinsurance cession example — the same pattern applies to any domain.

# Overview

The pattern has three layers:

1. **Data Model** — define domain types and import data (CSV, Excel, API)
2. **Business Rules** — pure C# logic operating on the data model
3. **Layout Areas** — reactive UI with charts, filters, and data grids

# Example: Reinsurance Claims Cession

## 1. Data Model

Define your domain types as simple records:

```csharp
// Claims data — imported from CSV
public record Claim(
    string ClaimId,
    string LineOfBusiness,
    double GrossAmount,
    double PaidAmount
);

// Reinsurance contract structure
public record ReinsuranceSection(
    string Id,
    string Name,
    string CoverType,        // "Proportional" or "ExcessOfLoss"
    double Cession,           // e.g., 0.7 for 70% quota share
    double AttachmentPoint,   // XL: deductible per claim
    double Limit,             // XL: max recovery per claim
    double? AggregateLimit    // Optional annual aggregate limit
);

// Cession result per claim
public record CededClaim(
    string ClaimId,
    string SectionId,
    double GrossAmount,
    double CededAmount,
    double RetainedAmount
);
```

## 2. Import Data with AddData

Register your data types and import from CSV in the hub configuration:

```csharp
public static MessageHubConfiguration AddReinsuranceHub(
    this MessageHubConfiguration config)
{
    return config
        .AddData(data => data
            .FromCsv<Claim>("claims.csv")          // Load claims from CSV
            .FromCsv<ReinsuranceSection>("sections.csv")
        )
        .WithHandler<ComputeCessionRequest>(HandleComputeCession);
}
```

CSV files are placed in the node's content collection and parsed automatically. The data is available via `workspace.GetStream<Claim>()`.

## 3. Business Rules — Cession Calculation

Business rules are pure C# — no framework dependencies. This is the core domain logic:

```csharp
public static class CessionEngine
{
    /// <summary>
    /// Calculates ceded amounts for all claims under a reinsurance section.
    /// Supports proportional (quota share) and non-proportional (excess of loss) covers.
    /// </summary>
    public static IReadOnlyList<CededClaim> ComputeCession(
        IReadOnlyList<Claim> claims,
        ReinsuranceSection section)
    {
        return claims.Select(claim =>
        {
            var ceded = section.CoverType switch
            {
                "Proportional" => ComputeProportionalCession(claim, section),
                "ExcessOfLoss" => ComputeExcessOfLossCession(claim, section),
                _ => 0.0
            };

            return new CededClaim(
                claim.ClaimId,
                section.Id,
                claim.GrossAmount,
                CededAmount: ceded,
                RetainedAmount: claim.GrossAmount - ceded
            );
        }).ToList();
    }

    /// <summary>
    /// Proportional (Quota Share): cede a fixed percentage of each claim.
    /// If a claims limit is set, cap the ceded amount.
    /// </summary>
    private static double ComputeProportionalCession(
        Claim claim, ReinsuranceSection section)
    {
        var ceded = claim.GrossAmount * section.Cession;
        if (section.Limit > 0)
            ceded = Math.Min(ceded, section.Limit);
        return ceded;
    }

    /// <summary>
    /// Non-Proportional (Excess of Loss): cede the amount above the attachment point,
    /// up to the layer limit. Formula: min(Limit, max(0, Gross - AttachmentPoint))
    /// </summary>
    private static double ComputeExcessOfLossCession(
        Claim claim, ReinsuranceSection section)
    {
        return Math.Min(
            section.Limit,
            Math.Max(0, claim.GrossAmount - section.AttachmentPoint)
        );
    }

    /// <summary>
    /// Apply an annual aggregate limit: cap total ceded across all claims.
    /// </summary>
    public static IReadOnlyList<CededClaim> ApplyAggregateLimit(
        IReadOnlyList<CededClaim> cededClaims, double aggregateLimit)
    {
        var running = 0.0;
        return cededClaims.Select(c =>
        {
            var available = Math.Max(0, aggregateLimit - running);
            var capped = Math.Min(c.CededAmount, available);
            running += capped;
            return c with
            {
                CededAmount = capped,
                RetainedAmount = c.GrossAmount - capped
            };
        }).ToList();
    }
}
```

For a production implementation with time series and data cubes, see [CededCashflows.cs](https://github.com/Systemorph/MeshWeaver.Reinsurance/blob/main/src/MeshWeaver.Reinsurance/Cession/CededCashflows.cs).

## 4. Layout Area — Interactive Charts

Layout areas render reactive UI with charts and filters. This example shows a PDF/CDF distribution chart with dropdown filters:

```csharp
public static class CessionLayoutArea
{
    /// <summary>
    /// Registers the cession results layout area on the hub.
    /// </summary>
    public static LayoutDefinition AddCessionResults(this LayoutDefinition layout)
        => layout.WithView(nameof(CessionResults), CessionResults);

    /// <summary>
    /// Reactive layout area: loads claims + sections from workspace,
    /// computes cession, and renders charts with filters.
    /// </summary>
    public static IObservable<UiControl> CessionResults(
        LayoutAreaHost host, RenderingContext ctx)
    {
        // Get reactive data streams — UI updates automatically when data changes
        var claimsStream = host.Workspace.GetStream<Claim>();
        var sectionsStream = host.Workspace.GetStream<ReinsuranceSection>();

        return claimsStream.CombineLatest(sectionsStream, (claims, sections) =>
        {
            if (claims == null || sections == null)
                return Controls.Markdown("Loading data...");

            return BuildDashboard(host, claims.ToList(), sections.ToList());
        });
    }

    private static UiControl BuildDashboard(
        LayoutAreaHost host,
        List<Claim> claims,
        List<ReinsuranceSection> sections)
    {
        // Compute cession for each section
        var allResults = sections
            .SelectMany(s => CessionEngine.ComputeCession(claims, s))
            .ToList();

        // Setup filter model with section dropdown
        var sectionOptions = sections
            .Select(s => new Option<string>(s.Id, s.Name))
            .ToList();
        host.UpdateData("SectionOptions", sectionOptions);

        var filterModel = new CessionFilterModel
        {
            SectionId = sections.FirstOrDefault()?.Id
        };
        host.UpdateData("CessionFilter", filterModel);

        // Build UI: title + filter toolbar + reactive chart
        return Controls.Stack
            .WithView(Controls.Title("Cession Analysis", 3))
            .WithView(host.Toolbar(filterModel, "CessionFilter"))
            .WithView(host.GetDataStream<CessionFilterModel>("CessionFilter")
                .Select(filter => BuildChart(allResults, filter, sections)));
    }

    private static UiControl BuildChart(
        List<CededClaim> results,
        CessionFilterModel filter,
        List<ReinsuranceSection> sections)
    {
        var filtered = results
            .Where(r => r.SectionId == filter.SectionId)
            .OrderBy(r => r.CededAmount)
            .ToList();

        if (!filtered.Any())
            return Controls.Markdown("No results for selected section.");

        var section = sections.First(s => s.Id == filter.SectionId);

        // Build histogram of ceded amounts
        var values = filtered.Select(r => r.CededAmount).ToArray();
        var nBins = 50;
        var min = values.Min();
        var max = values.Max();
        var binWidth = (max - min) / nBins;
        var histogram = new double[nBins];

        foreach (var v in values)
        {
            var bin = Math.Min((int)((v - min) / binWidth), nBins - 1);
            histogram[bin] += 1.0 / values.Length;
        }

        var labels = Enumerable.Range(0, nBins)
            .Select(i => (min + (i + 0.5) * binWidth).ToString("N0"));

        // Summary statistics
        var stats = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithView(Controls.Markdown(
                $"**Total Claims:** {filtered.Count}  \n" +
                $"**Total Gross:** {filtered.Sum(r => r.GrossAmount):N0}  \n" +
                $"**Total Ceded:** {filtered.Sum(r => r.CededAmount):N0}  \n" +
                $"**Cession Ratio:** {filtered.Sum(r => r.CededAmount) / filtered.Sum(r => r.GrossAmount):P1}"));

        // Chart
        var chart = Chart.Create(DataSet.Bar(histogram))
            .WithLabels(labels)
            .WithTitle($"Ceded Distribution — {section.Name}")
            .ToControl()
            .WithStyle("width: 100%; height: 400px;");

        return Controls.Stack
            .WithView(stats)
            .WithView(chart);
    }

    /// <summary>
    /// Filter model bound to the toolbar dropdowns.
    /// </summary>
    public record CessionFilterModel
    {
        [Dimension<string>(Options = "SectionOptions")]
        public string SectionId { get; init; }
    }
}
```

For a production implementation with CDF/PDF toggle, multi-dimensional filters, and tree-based structure navigation, see [DistributionLayoutArea.cs](https://github.com/Systemorph/MeshWeaver.Reinsurance/blob/main/src/MeshWeaver.Reinsurance.Pricing/LayoutAreas/DistributionLayoutArea.cs).

## 5. Wire It All Together

Register everything in the hub configuration:

```csharp
public static MessageHubConfiguration AddReinsuranceHub(
    this MessageHubConfiguration config)
{
    return config
        .AddData(data => data
            .FromCsv<Claim>("claims.csv")
            .FromCsv<ReinsuranceSection>("sections.csv")
        )
        .AddLayout(layout => layout
            .AddCessionResults()
        );
}
```

# Key Patterns

| Pattern | Description |
|---------|-------------|
| **Data Import** | `.AddData(data => data.FromCsv<T>("file.csv"))` loads typed data from content collections |
| **Reactive Streams** | `host.Workspace.GetStream<T>()` returns `IObservable` — UI updates automatically |
| **Pure Business Logic** | Calculation engines are plain C# with no framework coupling |
| **Filter Toolbar** | `host.Toolbar(model, dataId)` + `host.GetDataStream<T>(dataId)` for reactive filtering |
| **Charts** | `Chart.Create(DataSet.Bar(...)).WithLabels(...).ToControl()` for histograms, scatter, line |
| **Layout Composition** | `Controls.Stack.WithView(...)` composes UI hierarchically |

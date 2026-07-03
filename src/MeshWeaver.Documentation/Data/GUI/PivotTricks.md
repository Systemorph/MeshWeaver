---
Name: Pivot tricks
Category: Documentation
Description: Pivot and slice patterns over an in-RAM cube — rows-by-X columns-by-Y grids, totals, top-N, and YoY deltas, each framed as the prompt that would produce it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18"/><path d="M9 3v18"/><path d="M13 13l4 4"/><path d="M17 13v4h-4"/></svg>
---

Pivoting is the fastest way from a flat list of facts to an answer-shaped table. Every cell on this page is **live and executable** over the same tiny in-RAM cube — a flat `Sale(Region, Product, Year, Amount)` array — and opens with the *prompt that would produce it*. The pivot API lives in `MeshWeaver.Layout.Pivot` (`ToPivotGrid` + `GroupRowsBy` / `GroupColumnsBy` / `Aggregate`); the chart-side twin of the same slicing idea is in [Charts at a glance](../ChartGallery).

## Rows by X, columns by Y

> *Prompt: "Pivot sales: regions as rows, years as columns, sum of amount."*

```csharp --render PivotBasicDemo --show-code
using MeshWeaver.Layout.Pivot;

record Sale(string Region, string Product, string Year, double Amount);

var sales = new[]
{
    new Sale("EMEA", "Espresso", "2024", 210), new Sale("EMEA", "Espresso", "2025", 260),
    new Sale("EMEA", "Grinder", "2024", 110),  new Sale("EMEA", "Grinder", "2025", 130),
    new Sale("Americas", "Espresso", "2024", 180), new Sale("Americas", "Espresso", "2025", 215),
    new Sale("Americas", "Grinder", "2024", 100),  new Sale("Americas", "Grinder", "2025", 120),
    new Sale("APAC", "Espresso", "2024", 90),  new Sale("APAC", "Espresso", "2025", 135),
    new Sale("APAC", "Grinder", "2024", 60),   new Sale("APAC", "Grinder", "2025", 70),
};

sales.ToPivotGrid(pivot => pivot
    .GroupRowsBy(s => s.Region)
    .GroupColumnsBy(s => s.Year)
    .Aggregate(s => s.Amount, agg => agg.WithFunction(AggregateFunction.Sum)))
```

## Totals — close the table

> *Prompt: "Same pivot, but add row and column totals so I can sanity-check the sums."*

```csharp --render PivotTotalsDemo --show-code
using MeshWeaver.Layout.Pivot;

record Sale(string Region, string Product, string Year, double Amount);

var sales = new[]
{
    new Sale("EMEA", "Espresso", "2024", 210), new Sale("EMEA", "Espresso", "2025", 260),
    new Sale("EMEA", "Grinder", "2024", 110),  new Sale("EMEA", "Grinder", "2025", 130),
    new Sale("Americas", "Espresso", "2024", 180), new Sale("Americas", "Espresso", "2025", 215),
    new Sale("Americas", "Grinder", "2024", 100),  new Sale("Americas", "Grinder", "2025", 120),
    new Sale("APAC", "Espresso", "2024", 90),  new Sale("APAC", "Espresso", "2025", 135),
    new Sale("APAC", "Grinder", "2024", 60),   new Sale("APAC", "Grinder", "2025", 70),
};

sales.ToPivotGrid(pivot => pivot
    .GroupRowsBy(s => s.Region)
    .GroupColumnsBy(s => s.Year)
    .Aggregate(s => s.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
    .WithRowTotals()
    .WithColumnTotals())
```

Two row dimensions work the same way — chain a second `GroupRowsBy(s => s.Product)` and the grid nests products under regions.

## Top-N — cut the long tail

Top-N is a LINQ fold **before** the render: aggregate, order, take, and hand the finished rows to a [DataGrid](../DataGrid).

> *Prompt: "Give me the top 3 region-product combinations by total sales, as a table."*

```csharp --render TopNDemo --show-code
record Sale(string Region, string Product, string Year, double Amount);

var sales = new[]
{
    new Sale("EMEA", "Espresso", "2024", 210), new Sale("EMEA", "Espresso", "2025", 260),
    new Sale("EMEA", "Grinder", "2024", 110),  new Sale("EMEA", "Grinder", "2025", 130),
    new Sale("Americas", "Espresso", "2024", 180), new Sale("Americas", "Espresso", "2025", 215),
    new Sale("Americas", "Grinder", "2024", 100),  new Sale("Americas", "Grinder", "2025", 120),
    new Sale("APAC", "Espresso", "2024", 90),  new Sale("APAC", "Espresso", "2025", 135),
    new Sale("APAC", "Grinder", "2024", 60),   new Sale("APAC", "Grinder", "2025", 70),
};

record TopRow(string Region, string Product, double Total);

var top3 = sales
    .GroupBy(s => (s.Region, s.Product))
    .Select(g => new TopRow(g.Key.Region, g.Key.Product, g.Sum(s => s.Amount)))
    .OrderByDescending(r => r.Total)
    .Take(3)
    .ToArray();

Controls.DataGrid(top3)
```

## YoY deltas — the comparison column

Year-over-year is a self-join on the year dimension: pivot the two years side by side in LINQ, compute the delta, render the finished comparison.

> *Prompt: "Compare 2025 vs 2024 sales per region and show the growth in percent."*

```csharp --render YoyDemo --show-code
record Sale(string Region, string Product, string Year, double Amount);

var sales = new[]
{
    new Sale("EMEA", "Espresso", "2024", 210), new Sale("EMEA", "Espresso", "2025", 260),
    new Sale("EMEA", "Grinder", "2024", 110),  new Sale("EMEA", "Grinder", "2025", 130),
    new Sale("Americas", "Espresso", "2024", 180), new Sale("Americas", "Espresso", "2025", 215),
    new Sale("Americas", "Grinder", "2024", 100),  new Sale("Americas", "Grinder", "2025", 120),
    new Sale("APAC", "Espresso", "2024", 90),  new Sale("APAC", "Espresso", "2025", 135),
    new Sale("APAC", "Grinder", "2024", 60),   new Sale("APAC", "Grinder", "2025", 70),
};

record YoyRow(string Region, double Y2024, double Y2025, string Growth);

var yoy = sales
    .GroupBy(s => s.Region)
    .Select(g =>
    {
        var y24 = g.Where(s => s.Year == "2024").Sum(s => s.Amount);
        var y25 = g.Where(s => s.Year == "2025").Sum(s => s.Amount);
        return new YoyRow(g.Key, y24, y25, $"{(y25 - y24) / y24:P1}");
    })
    .OrderByDescending(r => r.Y2025)
    .ToArray();

Controls.DataGrid(yoy)
```

The same fold feeds a chart directly — pipe `yoy` into the `SliceBy(...).ToColumnChart(...)` pipeline from [Charts at a glance](../ChartGallery) and the comparison becomes grouped columns.

Try these in the Agentic Engineering exercises.

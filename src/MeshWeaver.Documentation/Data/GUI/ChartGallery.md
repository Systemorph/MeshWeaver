---
Name: Charts at a glance
Category: Documentation
Description: A gallery of the Charts API — column, bar, line, pie, and stacked charts from small in-RAM arrays, each framed as the prompt that would produce it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3v18h18"/><rect x="7" y="12" width="3" height="6"/><rect x="12" y="8" width="3" height="10"/><rect x="17" y="5" width="3" height="13"/></svg>
---

Every chart on this page is a **live, executable cell**: a handful of in-RAM values, one fluent `Charts.*` call, and the rendered chart beneath it. Each cell opens with the *prompt that would produce it* — the exact sentence you could hand an agent to get this chart back as a code cell. The API lives in `MeshWeaver.Layout.Chart` ([`ChartControl`](/Doc/GUI/DisplayControls) family); for slicing a dataset into series first, see the pivot side in [Pivot tricks](../PivotTricks).

## Column — compare categories

> *Prompt: "Show me quarterly revenue for this year as a column chart."*

```csharp --render ColumnDemo --show-code
using MeshWeaver.Layout.Chart;

// Four quarters of revenue (CHF k) — plain arrays are all a chart needs.
var revenue = new double[] { 480, 520, 610, 730 };
var quarters = new[] { "Q1", "Q2", "Q3", "Q4" };

Charts.Column(revenue, quarters, "Revenue (CHF k)")
    .WithTitle("Quarterly revenue")
```

## Bar — compare categories with long labels

> *Prompt: "Rank our product lines by units sold, horizontal bars so the names stay readable."*

```csharp --render BarDemo --show-code
using MeshWeaver.Layout.Chart;

var units = new double[] { 1240, 990, 640, 410 };
var products = new[] { "Espresso machines", "Grinders", "Filter kits", "Accessories" };

Charts.Bar(units, products, "Units sold")
    .WithTitle("Product lines by units")
```

## Line — a trend over time

> *Prompt: "Plot monthly active users for the first half of the year as a line."*

```csharp --render LineDemo --show-code
using MeshWeaver.Layout.Chart;

var mau = new double[] { 3.1, 3.4, 3.9, 4.6, 5.2, 6.0 };
var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun" };

Charts.Line(mau, months, "MAU (k)")
    .WithTitle("Monthly active users")
```

## Pie — shares of a whole

> *Prompt: "What's our traffic split by channel? Pie chart, one slice per channel."*

```csharp --render PieDemo --show-code
using MeshWeaver.Layout.Chart;

var visits = new double[] { 44, 27, 18, 11 };
var channels = new[] { "Organic", "Paid", "Referral", "Direct" };

Charts.Pie(visits, channels)
    .WithTitle("Traffic by channel")
```

## Stacked column — two dimensions in one chart

For two dimensions — categories on the axis, a second dimension stacked inside each column — slice the rows first, then chart the slices. This is the same `SliceBy` pipeline the [data-cube pages](/Doc/DataMesh/DataCubes) build on.

> *Prompt: "Break revenue down by region AND year: one column per year, stacked by region."*

```csharp --render StackedDemo --show-code
using MeshWeaver.Layout.Chart;

record Sale(string Region, string Year, double Amount);

var sales = new[]
{
    new Sale("EMEA", "2024", 320), new Sale("EMEA", "2025", 390),
    new Sale("Americas", "2024", 280), new Sale("Americas", "2025", 335),
    new Sale("APAC", "2024", 150), new Sale("APAC", "2025", 205),
};

sales
    .SliceBy(s => s.Year)     // one column per year
    .SliceBy(s => s.Region)   // stacked by region inside each column
    .ToStackedColumnChart(g => g.Sum(s => s.Amount))
    .WithTitle("Revenue by year, stacked by region (CHF k)")
```

Swap `ToStackedColumnChart` for `ToColumnChart` (grouped columns), `ToBarChart` / `ToStackedBarChart` (horizontal), or `ToLineChart` (one line per slice) — the slicing stays identical.

Try these in the Agentic Engineering exercises.

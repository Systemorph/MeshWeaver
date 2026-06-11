---
Name: Data Cubes — Dimensions, FX Conversion, Slice & Dice
Category: Documentation
Description: Model a simple data cube with dimension attributes — dimensions as mesh nodes, FX conversion to a group currency, the Edit form and MeshNodePicker dialogs, and live slice-and-dice pivot tables and charts.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2l8 4.5v9L12 20l-8-4.5v-9L12 2z"/><path d="M12 2v9m0 0l8-4.5M12 11l-8-4.5M12 20v-9"/></svg>
---

# Data Cubes

A **data cube** is the simplest useful analytics shape: facts keyed by a handful of **dimensions**, carrying one or more **measures**. In MeshWeaver the whole cube is mesh content — the fact type is a Code node, the dimensions are mesh nodes, and the analytics are layout areas you can open, edit, and pick from like any other node.

This page builds a complete one — **LineOfBusiness × Year × Currency → Amount**, with FX conversion into the group currency (CHF) — and renders every claim live. The working node lives in the FutuRe sample; every number below is pinned by `FxCubeExampleTest` in `test/MeshWeaver.Documentation.Test`.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 300" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif">
  <defs>
    <marker id="dc-arr" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#90a4ae"/></marker>
  </defs>
  <path d="M120 90l80-40 100 30-80 45-100-35z" fill="#5c6bc0"/>
  <path d="M120 90l100 35v80l-100-35v-80z" fill="#3949ab"/>
  <path d="M220 125l80-45v80l-80 45v-80z" fill="#283593"/>
  <text x="200" y="72" text-anchor="middle" font-size="11" fill="#fff">Year</text>
  <text x="150" y="160" text-anchor="middle" font-size="11" fill="#c5cae9" transform="rotate(19,150,160)">Line of Business</text>
  <text x="268" y="160" text-anchor="middle" font-size="11" fill="#9fa8da" transform="rotate(-29,268,160)">Currency</text>
  <line x1="320" y1="140" x2="420" y2="140" stroke="#f57c00" stroke-width="2" marker-end="url(#dc-arr)"/>
  <text x="370" y="130" text-anchor="middle" font-size="10" fill="#f57c00">FX → CHF</text>
  <rect x="430" y="40" width="300" height="60" rx="10" fill="#1b5e20"/>
  <text x="580" y="65" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Slice</text>
  <text x="580" y="84" text-anchor="middle" font-size="10" fill="#a5d6a7">group one dimension, total the measure</text>
  <rect x="430" y="115" width="300" height="60" rx="10" fill="#0d47a1"/>
  <text x="580" y="140" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Dice</text>
  <text x="580" y="159" text-anchor="middle" font-size="10" fill="#90caf9">fix members → a smaller cube</text>
  <rect x="430" y="190" width="300" height="60" rx="10" fill="#4a148c"/>
  <text x="580" y="215" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Pivot & Chart</text>
  <text x="580" y="234" text-anchor="middle" font-size="10" fill="#ce93d8">the same grouping, rendered live</text>
</svg>

## The representation — everything is a mesh node

The cube ships exactly like every domain in the samples (`samples/Graph/Data/FutuRe/`): a **NodeType node** declares the type, its **`Source/*.cs` files are Code nodes** compiled at runtime, and **dimension members are mesh nodes** of their own:

```text
FutuRe/
├── FxCube.json                    ← NodeType node ("config => config.ConfigureFxCube()")
├── FxCube/Source/
│   ├── FxCube.cs                  ← Code node: fact record, FX engine, sample facts, hub config
│   └── FxCubeLayoutAreas.cs       ← Code node: pivot / chart / Edit / dialog views
├── LineOfBusiness.json            ← the dimension's NodeType node
├── LineOfBusiness/Source/…        ← the dimension's Code nodes
└── AmericasIns/LineOfBusiness/
    ├── AGRICULTURE.json           ← a dimension MEMBER — a mesh node
    └── …
```

Open `FutuRe/FxCube` in a portal with the samples loaded and the views below are its layout areas.

## 1. Model the fact — dimension attributes

```csharp
public record FxCubeFact
{
    [Key]
    public string Id { get; init; } = string.Empty;          // "Property-2024-EUR"

    [Dimension(typeof(string), nameof(LineOfBusiness))]
    public string LineOfBusiness { get; init; } = string.Empty;

    [Dimension(typeof(string), nameof(Year))]
    public string Year { get; init; } = string.Empty;

    [Dimension(typeof(string), nameof(Currency))]
    public string Currency { get; init; } = string.Empty;

    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double Amount { get; init; }                       // local currency
}
```

The `[Dimension]` attributes do double duty: the **pivot builder** discovers these columns as groupable fields, and the **Edit form** renders them as dropdowns. See [Data Modeling](/Doc/DataMesh/DataModeling) for typed dimension records (`[Dimension<Country>]`) backed by a data source.

## 2. Dimensions are mesh nodes

A dimension member is a node — `FutuRe/AmericasIns/LineOfBusiness/AGRICULTURE` is a `FutuRe/LineOfBusiness` node with content, description, governance, and its own page. That means *picking a dimension member is picking a node*: put `[MeshNode]` on a path-valued property and the Edit form renders the searchable **MeshNodePicker** over the dimension nodes:

```csharp
public record FxCubeFactDraft
{
    [MeshNode("nodeType:FutuRe/LineOfBusiness")]      // ← picker over the dimension NODES
    [Display(Name = "Line of Business (mesh node)")]
    public string? LineOfBusinessPath { get; init; }   //   stores the node PATH

    [UiControl<SelectControl>(Options = new[] { "2024", "2025" })]
    public string Year { get; init; } = "2025";

    [UiControl<SelectControl>(Options = new[] { "CHF", "EUR", "USD" })]
    public string Currency { get; init; } = "CHF";

    public double Amount { get; init; }
}
```

Use `[Dimension<T>]` when members are typed reference data in a workspace; use `[MeshNode("query")]` when members are mesh nodes with lives of their own (pages, governance, access control). The FutuRe lines of business are the latter. Full picker options (layout, open direction, default-to-first): [Property Attributes](/Doc/GUI/Attributes).

## 3. FX conversion — a pure function over the facts

Local amounts convert into the group currency at **plan** (budget) or **actual** (market) rates — the same shape the full FutuRe profitability cube uses, distilled:

```csharp
public static IReadOnlyList<FxCubeFact> ConvertToGroupCurrency(
    IEnumerable<FxCubeFact> facts, FxMode mode)
    => facts.Select(f => f with
        {
            Amount = f.Amount * RateToChf(f.Currency, mode),   // CHF 1.00 · EUR .95/.93 · USD .90/.88
            Currency = "CHF",
        })
        .ToList();
```

| Rates → CHF | Plan | Actual |
|---|---|---|
| CHF | 1.00 | 1.00 |
| EUR | 0.95 | 0.93 |
| USD | 0.90 | 0.88 |

## 4. Edit on the GUI — one call, no per-field code

`host.Edit(instance)` renders the whole form from the record's attributes. Live, from the kernel:

```csharp --render FxCubeEditDemo --show-code
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Layout;

record FxCubeFactDraft
{
    [UiControl<SelectControl>(Options = new[] { "Property", "Casualty", "Specialty" })]
    [Display(Name = "Line of Business")]
    public string LineOfBusiness { get; init; } = "Property";

    [UiControl<SelectControl>(Options = new[] { "2024", "2025" })]
    public string Year { get; init; } = "2025";

    [UiControl<SelectControl>(Options = new[] { "CHF", "EUR", "USD" })]
    public string Currency { get; init; } = "EUR";

    [DisplayFormat(DataFormatString = "{0:N0}")]
    public double Amount { get; init; } = 220_000;
}

Mesh.Edit(new FxCubeFactDraft(), "fxCubeDraft")
```

## 5. The picker in a dialog

Opening the same form as a **modal dialog** is one click action: build the dialog, write it to the dialog area. (In the FutuRe sample this is the `NewFactDialog` view — its draft uses the real `[MeshNode]` picker over the LineOfBusiness nodes.)

```csharp
Controls.Button("New fact…")
    .WithClickAction(click =>
    {
        var dialog = Controls.Dialog(
                click.Host.Edit(new FxCubeFactDraft(), "newFact"),
                "New FX Cube Fact")
            .WithSize("M")
            .WithClosable(true);
        click.Host.UpdateArea(DialogControl.DialogArea, dialog);   // open
        return Task.CompletedTask;
    });
// close from any action: ctx.Host.UpdateArea(DialogControl.DialogArea, null!);
```

Reactive dialog patterns (spinners, conditional sections, server round-trips): [Reactive Dialogs](/Doc/GUI/ReactiveDialogs).

## 6. Slice & dice — the pivot table

Rows by Line of Business, columns by Year, plan-CHF amounts — with totals. Live:

```csharp --render FxCubePivotDemo --show-code
using MeshWeaver.Layout.Pivot;

record Fact(string LineOfBusiness, string Year, string Currency, double Amount);

var facts = new[]
{
    new Fact("Property", "2024", "CHF", 100_000), new Fact("Property", "2025", "CHF", 120_000),
    new Fact("Property", "2024", "EUR", 200_000), new Fact("Property", "2025", "EUR", 220_000),
    new Fact("Property", "2024", "USD", 300_000), new Fact("Property", "2025", "USD", 320_000),
    new Fact("Casualty", "2024", "CHF",  80_000), new Fact("Casualty", "2025", "CHF",  90_000),
    new Fact("Casualty", "2024", "EUR", 160_000), new Fact("Casualty", "2025", "EUR", 170_000),
    new Fact("Casualty", "2024", "USD", 240_000), new Fact("Casualty", "2025", "USD", 250_000),
    new Fact("Specialty", "2024", "CHF", 50_000), new Fact("Specialty", "2025", "CHF", 60_000),
    new Fact("Specialty", "2024", "EUR", 100_000), new Fact("Specialty", "2025", "EUR", 110_000),
    new Fact("Specialty", "2024", "USD", 150_000), new Fact("Specialty", "2025", "USD", 160_000),
};

double PlanRate(string ccy) => ccy switch { "EUR" => 0.95, "USD" => 0.90, _ => 1.00 };
var planChf = facts.Select(f => f with { Amount = f.Amount * PlanRate(f.Currency), Currency = "CHF" });

planChf.ToPivotGrid(pivot => pivot
    .GroupRowsBy(f => f.LineOfBusiness)
    .GroupColumnsBy(f => f.Year)
    .Aggregate(f => f.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
    .WithRowTotals()
    .WithColumnTotals())
```

Re-dice it yourself: the field picker on the grid lets you drag `Currency` into rows or columns — the cube re-aggregates live.

## 7. Slice & dice — the charts

The same grouping, charted. Stacked columns — one series per Line of Business across the years:

```csharp --render FxCubeStackedDemo --show-code
using MeshWeaver.Layout.Chart;

record Fact(string LineOfBusiness, string Year, string Currency, double Amount);

var facts = new[]
{
    new Fact("Property", "2024", "CHF", 100_000), new Fact("Property", "2025", "CHF", 120_000),
    new Fact("Property", "2024", "EUR", 200_000), new Fact("Property", "2025", "EUR", 220_000),
    new Fact("Property", "2024", "USD", 300_000), new Fact("Property", "2025", "USD", 320_000),
    new Fact("Casualty", "2024", "CHF",  80_000), new Fact("Casualty", "2025", "CHF",  90_000),
    new Fact("Casualty", "2024", "EUR", 160_000), new Fact("Casualty", "2025", "EUR", 170_000),
    new Fact("Casualty", "2024", "USD", 240_000), new Fact("Casualty", "2025", "USD", 250_000),
    new Fact("Specialty", "2024", "CHF", 50_000), new Fact("Specialty", "2025", "CHF", 60_000),
    new Fact("Specialty", "2024", "EUR", 100_000), new Fact("Specialty", "2025", "EUR", 110_000),
    new Fact("Specialty", "2024", "USD", 150_000), new Fact("Specialty", "2025", "USD", 160_000),
};

double PlanRate(string ccy) => ccy switch { "EUR" => 0.95, "USD" => 0.90, _ => 1.00 };
var planChf = facts.Select(f => f with { Amount = f.Amount * PlanRate(f.Currency) });

planChf
    .SliceBy(f => f.Year)
    .SliceBy(f => f.LineOfBusiness)
    .ToStackedColumnChart(g => g.Sum(f => f.Amount))
    .WithTitle("Plan (CHF) by Year and Line of Business")
```

And the original-currency split as a pie — slicing the *unconverted* cube by Currency:

```csharp --render FxCubePieDemo --show-code
using MeshWeaver.Layout.Chart;

record Fact(string Currency, double Amount);

var facts = new[]
{
    new Fact("CHF", 500_000),     // 100+120+80+90+50+60 k
    new Fact("EUR", 960_000),     // 200+220+160+170+100+110 k
    new Fact("USD", 1_420_000),   // 300+320+240+250+150+160 k
};

facts
    .SliceBy(f => f.Currency)
    .ToPieChart(g => g.Sum(f => f.Amount))
    .WithTitle("Local Amounts by Currency")
```

## 8. The numbers — pinned by tests

Every figure this page renders is asserted in `FxCubeExampleTest` (`test/MeshWeaver.Documentation.Test`), against the same engine that ships in the sample's Code node:

| Slice | Value |
|---|---:|
| Local grand total | 2,880,000 |
| Plan-CHF grand total | 2,690,000 |
| Actual-CHF grand total | 2,642,400 |
| Plan-CHF 2024 / 2025 | 1,288,000 / 1,402,000 |
| Plan-CHF Property / Casualty / Specialty | 1,177,000 / 924,500 / 588,500 |
| Dice Property × EUR, plan-CHF | 399,000 |

## See Also

- [Data Modeling](/Doc/DataMesh/DataModeling) — typed dimensions, `[Dimension<T>]`, reference data
- [Property Attributes](/Doc/GUI/Attributes) — `[MeshNode]`, `[UiControl<T>]`, the full attribute catalogue
- [Editor Control](/Doc/GUI/Editor) — how `Edit` maps records to forms
- [Reactive Dialogs](/Doc/GUI/ReactiveDialogs) — dialogs beyond a single form
- [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes) — NodeType nodes + Source code nodes, end to end

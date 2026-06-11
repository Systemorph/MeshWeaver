---
Name: Data Cubes — A Pension Fund Balance Sheet
Category: Documentation
Description: Model a data cube where everything is a mesh node — dimension types with instances, facts without ids, computed positions with formulas evaluated by business-rules scopes, and live slice-and-dice pivot tables and charts.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2l8 4.5v9L12 20l-8-4.5v-9L12 2z"/><path d="M12 2v9m0 0l8-4.5M12 11l-8-4.5M12 20v-9"/></svg>
---

# Data Cubes

A **data cube** is the simplest useful analytics shape: facts keyed by a handful of **dimensions**, carrying one or more **measures**. In MeshWeaver the whole cube is mesh content — the dimension *types* are NodeType nodes, the dimension *members* are mesh nodes, the facts are mesh nodes, and even the *formulas* are data on dimension nodes.

This page builds a complete one: the balance sheet of **Helvetia Vorsorge**, a fictional Swiss pension fund — **Position × Year × Currency → Amount**, with computed positions like *Total Assets* and the *Funding Ratio* modelled **out of** the atomic positions and evaluated by business-rules scopes. The working node set ships in `samples/Graph/Data/PensionFund/`; every number below is pinned by `PensionFundExampleTest` in `test/MeshWeaver.Documentation.Test`.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 300" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif">
  <defs>
    <marker id="dc-arr" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#90a4ae"/></marker>
  </defs>
  <path d="M120 90l80-40 100 30-80 45-100-35z" fill="#5c6bc0"/>
  <path d="M120 90l100 35v80l-100-35v-80z" fill="#3949ab"/>
  <path d="M220 125l80-45v80l-80 45v-80z" fill="#283593"/>
  <text x="200" y="72" text-anchor="middle" font-size="11" fill="#fff">Year</text>
  <text x="150" y="160" text-anchor="middle" font-size="11" fill="#c5cae9" transform="rotate(19,150,160)">Position</text>
  <text x="268" y="160" text-anchor="middle" font-size="11" fill="#9fa8da" transform="rotate(-29,268,160)">Currency</text>
  <line x1="320" y1="140" x2="420" y2="140" stroke="#f57c00" stroke-width="2" marker-end="url(#dc-arr)"/>
  <text x="370" y="130" text-anchor="middle" font-size="10" fill="#f57c00">scopes</text>
  <rect x="430" y="40" width="300" height="60" rx="10" fill="#1b5e20"/>
  <text x="580" y="65" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Atomic</text>
  <text x="580" y="84" text-anchor="middle" font-size="10" fill="#a5d6a7">value comes from a fact entry node</text>
  <rect x="430" y="115" width="300" height="60" rx="10" fill="#0d47a1"/>
  <text x="580" y="140" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Sum</text>
  <text x="580" y="159" text-anchor="middle" font-size="10" fill="#90caf9">Σ weight · value(component position)</text>
  <rect x="430" y="190" width="300" height="60" rx="10" fill="#4a148c"/>
  <text x="580" y="215" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Ratio</text>
  <text x="580" y="234" text-anchor="middle" font-size="10" fill="#ce93d8">numerator ÷ denominator — the funding ratio</text>
</svg>

## The representation — everything is a mesh node

The cube ships exactly like every domain in the samples: each **dimension gets its own NodeType node**, its members are mesh nodes of that type, the **`Source/*.cs` files are Code nodes** compiled at runtime, and the facts are mesh nodes too:

```text
PensionFund/
├── Position.json                   ← the dimension's NodeType node
├── Position/
│   ├── Source/Position.cs          ← Code node: the dimension record + formula model
│   ├── Cash.json … FreeFunds.json  ← 15 atomic position MEMBERS — mesh nodes
│   └── TotalAssets.json …          ← 6 COMPUTED positions — formulas as node content
├── Year.json · Year/2024.json …    ← reporting years
├── Currency.json · Currency/CHF.json …
├── BalanceSheetEntry.json
├── BalanceSheetEntry/2024-Cash.json …   ← 30 facts — one node per Position × Year
└── BalanceSheet.json               ← "config => config.ConfigureBalanceSheet()"
    └── BalanceSheet/Source/        ← scopes, data loader, layout areas
```

There is **no Id property anywhere** — a mesh node's identity is its **path**. A fact references its dimensions by *their* paths, and a formula references its operand positions by path. Open `PensionFund/BalanceSheet` in a portal with the samples loaded and the views below are its layout areas.

## 1. Dimension types host their instances

Each dimension is declared by a NodeType node whose `Source/` holds the content record. The Position dimension is the interesting one — it carries the **formula model**:

```csharp
public enum BalanceSheetSide { Assets, Liabilities, Computed }
public enum PositionAggregation { Atomic, Sum, Ratio }

public record PositionComponent
{
    [MeshNode("nodeType:PensionFund/Position")]   // ← references another Position NODE
    public string Position { get; init; } = string.Empty;
    public double Weight { get; init; } = 1;       //   +1 adds, −1 subtracts
}

public record Position
{
    public BalanceSheetSide Side { get; init; }
    public PositionAggregation Aggregation { get; init; }
    public PositionComponent[]? Components { get; init; }   // Sum operands

    [MeshNode("nodeType:PensionFund/Position")]
    public string? Numerator { get; init; }                  // Ratio
    [MeshNode("nodeType:PensionFund/Position")]
    public string? Denominator { get; init; }
}
```

Note what the record does **not** carry: no `Id`, no `Name`, no `Description`, no `Order` — all of those already live on the **mesh node itself** (`MeshNode.Name`, `MeshNode.Description`, `MeshNode.Order`). The content holds only what the node doesn't have: the formula model.

The `[MeshNode("query")]` attribute does double duty: it documents that the property holds a **node path**, and the Edit form renders it as the searchable **MeshNodePicker** over exactly the nodes the query matches — here, the members of the Position dimension.

## 2. Formulas are data on dimension nodes

Computed positions are ordinary Position nodes whose content holds the formula. *Pension Capital* — the actuarial obligation — is the sum of three other positions:

```json
{
  "id": "PensionCapital",
  "namespace": "PensionFund/Position",
  "nodeType": "PensionFund/Position",
  "content": {
    "$type": "Position",
    "side": "Computed",
    "aggregation": "Sum",
    "components": [
      { "position": "PensionFund/Position/ActiveMembersCapital", "weight": 1 },
      { "position": "PensionFund/Position/PensionersCapital",   "weight": 1 },
      { "position": "PensionFund/Position/TechnicalProvisions", "weight": 1 }
    ]
  }
}
```

*Available Assets* uses **negative weights** (total assets minus short-term obligations), and the *Funding Ratio* is a `Ratio` position dividing it by *Pension Capital* — the statutory solvency measure of a Swiss pension fund (BVV2 Art. 44). Editing a formula is editing a node: add a component in the GUI and every report recomputes.

## 3. The fact — no Id, dimension columns are node paths

```csharp
public record BalanceSheetEntry
{
    [MeshNode("nodeType:PensionFund/Position")]
    public string Position { get; init; } = string.Empty;   // a node PATH

    [MeshNode("nodeType:PensionFund/Year")]
    public string Year { get; init; } = string.Empty;

    [MeshNode("nodeType:PensionFund/Currency")]
    public string Currency { get; init; } = string.Empty;

    [DisplayFormat(DataFormatString = "{0:N1}")]
    public double Amount { get; init; }                      // CHF m
}
```

Thirty entries — 15 atomic positions × 2 years — make up the sample balance sheet, and both years balance by construction (2024: 1,060.0 · 2025: 1,142.0).

## 4. Business rules — scopes evaluate any position

The evaluation engine is one interface from the **business-rules framework** (`MeshWeaver.BusinessRules`): a scope per (position, year), composing other scopes for its operands. The scope *generator* emits the implementations at build time — for sample Code nodes, the NodeType compiler runs it during dynamic compilation:

```csharp
public record PositionYear(string Position, string Year);

public interface PositionValue : IScope<PositionYear, BalanceSheetStorage>
{
    Position Position => GetStorage().Positions[Identity.Position];

    double Value => Position.Aggregation switch
    {
        PositionAggregation.Atomic =>
            GetStorage().Amounts.TryGetValue((Identity.Position, Identity.Year), out var v) ? v : 0,

        PositionAggregation.Sum =>
            (Position.Components ?? [])
                .Sum(c => c.Weight * GetScope<PositionValue>(new PositionYear(c.Position, Identity.Year)).Value),

        PositionAggregation.Ratio =>
            GetScope<PositionValue>(new PositionYear(Position.Denominator!, Identity.Year)).Value is var d && d != 0
                ? GetScope<PositionValue>(new PositionYear(Position.Numerator!, Identity.Year)).Value / d
                : 0,

        _ => 0,
    };
}
```

Scope instances are **cached per identity** — *Total Assets* feeds both *Available Assets* and the balance check, yet is computed once. Registration is one line in the node's hub configuration:

```csharp
config.WithServices(services => services.AddBusinessRules(typeof(PositionValue).Assembly))
```

and any view evaluates positions through the registry:

```csharp
var registry = host.Hub.ServiceProvider.CreateScopeRegistry(storage);
var ratio = registry.GetScope<PositionValue>(
    new PositionYear("PensionFund/Position/FundingRatio", "PensionFund/Year/2024")).Value;   // ≈ 109.8%
```

The same recursion, runnable right here — atomic values and formulas exactly as in the sample nodes, folded the way the `PositionValue` scope does:

```csharp --render PensionEvalDemo --show-code
// The formula model: Sum positions fold weighted components, Ratio divides.
record Component(string Position, double Weight);
record Pos(string Agg, Component[]? Components = null, string? Num = null, string? Den = null);

var amounts = new Dictionary<string, double>          // 2024 atomic facts, CHF m
{
    ["Cash"] = 50, ["Bonds"] = 400, ["Equities"] = 300, ["RealEstate"] = 200,
    ["Alternatives"] = 100, ["Receivables"] = 10,
    ["Payables"] = 15, ["AccruedLiabilities"] = 5, ["EmployerContributionReserve"] = 20,
    ["NonTechnicalProvisions"] = 10, ["ActiveMembersCapital"] = 600,
    ["PensionersCapital"] = 280, ["TechnicalProvisions"] = 40,
    ["ValueFluctuationReserve"] = 80, ["FreeFunds"] = 10,
};

Component[] Sum1(params string[] ps) => ps.Select(p => new Component(p, 1)).ToArray();
var positions = new Dictionary<string, Pos>
{
    ["TotalAssets"]     = new("Sum", Sum1("Cash", "Bonds", "Equities", "RealEstate", "Alternatives", "Receivables")),
    ["PensionCapital"]  = new("Sum", Sum1("ActiveMembersCapital", "PensionersCapital", "TechnicalProvisions")),
    ["AvailableAssets"] = new("Sum", new[] { new Component("TotalAssets", 1),
        new Component("Payables", -1), new Component("AccruedLiabilities", -1),
        new Component("EmployerContributionReserve", -1), new Component("NonTechnicalProvisions", -1) }),
    ["FundingRatio"]    = new("Ratio", Num: "AvailableAssets", Den: "PensionCapital"),
};

double Value(string p) => positions.TryGetValue(p, out var pos)
    ? pos.Agg switch
    {
        "Sum"   => pos.Components!.Sum(c => c.Weight * Value(c.Position)),
        "Ratio" => Value(pos.Num!) / Value(pos.Den!),
        _ => 0,
    }
    : amounts[p];                                      // Atomic: read the fact

Controls.Markdown($"""
| Computed position (2024) | Value |
|---|---:|
| Total Assets | {Value("TotalAssets"):N1} |
| Pension Capital | {Value("PensionCapital"):N1} |
| Available Assets | {Value("AvailableAssets"):N1} |
| **Funding Ratio** | **{Value("FundingRatio"):P1}** |
""")
```

## 5. Edit on the GUI — one call, no per-field code

`host.Edit(instance)` renders the whole form from the record's attributes. In the sample, the dimension fields carry `[MeshNode]` and render node pickers; here, live from the kernel with selects:

```csharp --render PensionEditDemo --show-code
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Layout;

record BalanceSheetEntryDraft
{
    [UiControl<SelectControl>(Options = new[] { "Cash", "Bonds", "Equities", "RealEstate" })]
    [Display(Name = "Position")]
    public string Position { get; init; } = "Equities";

    [UiControl<SelectControl>(Options = new[] { "2024", "2025" })]
    public string Year { get; init; } = "2025";

    [UiControl<SelectControl>(Options = new[] { "CHF", "EUR", "USD" })]
    public string Currency { get; init; } = "CHF";

    [DisplayFormat(DataFormatString = "{0:N1}")]
    [Display(Name = "Amount (CHF m)")]
    public double Amount { get; init; } = 340.0;
}

Mesh.Edit(new BalanceSheetEntryDraft(), "pensionDraft")
```

## 6. The picker in a dialog

Opening the same form as a **modal dialog** is one click action — build the dialog, write it to the dialog area. In the sample this is the `NewEntryDialog` view, whose draft uses the real `[MeshNode]` pickers over the Position / Year / Currency nodes:

```csharp
Controls.Button("New balance sheet entry…")
    .WithClickAction(click =>
    {
        var dialog = Controls.Dialog(
                click.Host.Edit(new BalanceSheetEntryDraft(), "newEntry"),
                "New Balance Sheet Entry")
            .WithSize("M")
            .WithClosable(true);
        click.Host.UpdateArea(DialogControl.DialogArea, dialog);   // open
        return Task.CompletedTask;
    });
// close from any action: ctx.Host.UpdateArea(DialogControl.DialogArea, null!);
```

Reactive dialog patterns (spinners, conditional sections, server round-trips): [Reactive Dialogs](/Doc/GUI/ReactiveDialogs).

## 7. Slice & dice — the pivot table

Rows by Position, columns by Year — with totals. Live:

```csharp --render PensionPivotDemo --show-code
using MeshWeaver.Layout.Pivot;

record Entry(string Position, string Year, double Amount);

var entries = new Dictionary<string, (double Y2024, double Y2025)>
{
    ["Cash"] = (50, 60), ["Bonds"] = (400, 410), ["Equities"] = (300, 340),
    ["RealEstate"] = (200, 210), ["Alternatives"] = (100, 110), ["Receivables"] = (10, 12),
}
.SelectMany(kvp => new[] { new Entry(kvp.Key, "2024", kvp.Value.Y2024), new Entry(kvp.Key, "2025", kvp.Value.Y2025) })
.ToArray();

entries.ToPivotGrid(pivot => pivot
    .GroupRowsBy(e => e.Position)
    .GroupColumnsBy(e => e.Year)
    .Aggregate(e => e.Amount, agg => agg.WithFunction(AggregateFunction.Sum))
    .WithRowTotals()
    .WithColumnTotals())
```

Re-dice it yourself: the field picker on the grid lets you drag dimensions between rows and columns — the cube re-aggregates live.

## 8. Slice & dice — the charts

The asset side, stacked by position across the years:

```csharp --render PensionStackedDemo --show-code
using MeshWeaver.Layout.Chart;

record Entry(string Position, string Year, double Amount);

var entries = new Dictionary<string, (double Y2024, double Y2025)>
{
    ["Cash"] = (50, 60), ["Bonds"] = (400, 410), ["Equities"] = (300, 340),
    ["RealEstate"] = (200, 210), ["Alternatives"] = (100, 110), ["Receivables"] = (10, 12),
}
.SelectMany(kvp => new[] { new Entry(kvp.Key, "2024", kvp.Value.Y2024), new Entry(kvp.Key, "2025", kvp.Value.Y2025) })
.ToArray();

entries
    .SliceBy(e => e.Year)
    .SliceBy(e => e.Position)
    .ToStackedColumnChart(g => g.Sum(e => e.Amount))
    .WithTitle("Assets by Year and Position (CHF m)")
```

And the 2025 asset allocation as a pie — the same chart the sample's `AssetAllocation` view renders from the scopes:

```csharp --render PensionPieDemo --show-code
using MeshWeaver.Layout.Chart;

record Entry(string Position, double Amount);

var assets2025 = new[]
{
    new Entry("Bonds", 410.0), new Entry("Equities", 340.0), new Entry("RealEstate", 210.0),
    new Entry("Alternatives", 110.0), new Entry("Cash", 60.0), new Entry("Receivables", 12.0),
};

assets2025
    .SliceBy(e => e.Position)
    .ToPieChart(g => g.Sum(e => e.Amount))
    .WithTitle("Asset Allocation 2025 (CHF m)")
```

## 9. The numbers — pinned by tests

Every figure this page shows is asserted in `PensionFundExampleTest` (`test/MeshWeaver.Documentation.Test`) — evaluated through the **real generated scopes**, the same engine the sample's Code nodes compile against:

| Figure | 2024 | 2025 |
|---|---:|---:|
| Total Assets = Balance Sheet Sum = Total Liabilities | 1,060.0 | 1,142.0 |
| Pension Capital | 920.0 | 964.0 |
| Available Assets | 1,010.0 | 1,084.0 |
| **Funding Ratio** | **≈ 109.8%** | **≈ 112.4%** |

## See Also

- [Data Modeling](/Doc/DataMesh/DataModeling) — typed dimensions, `[Dimension<T>]`, reference data
- [Property Attributes](/Doc/GUI/Attributes) — `[MeshNode]`, `[UiControl<T>]`, the full attribute catalogue
- [Editor Control](/Doc/GUI/Editor) — how `Edit` maps records to forms
- [Reactive Dialogs](/Doc/GUI/ReactiveDialogs) — dialogs beyond a single form
- [Creating Node Types](/Doc/DataMesh/CreatingNodeTypes) — NodeType nodes + Source code nodes, end to end

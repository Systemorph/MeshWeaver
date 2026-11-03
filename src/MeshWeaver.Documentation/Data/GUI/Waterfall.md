---
Name: Waterfall (Bridge) Charts
Category: Documentation
Description: WaterfallControl — an opening amount, the signed deltas hanging from the running level, and the result standing on the axis again; geometry resolved server-side, data-bindable.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 21h18"/><rect x="4" y="4" width="3" height="17"/><rect x="9" y="4" width="3" height="7"/><rect x="14" y="11" width="3" height="4"/><rect x="19" y="15" width="2" height="6"/></svg>
---

`WaterfallControl` draws a **bridge**: from an opening amount, through the changes that make it up,
to a result. Premium → expected loss → costs → technical result is the typical one; gross → ceded →
net, or plan → variances → actual, are others. Never draw this as hand-built SVG in `Controls.Html` —
compose the control.

```csharp
Controls.Waterfall(
        WaterfallStep.Total("Our premium", 262_500),
        WaterfallStep.Delta("Expected loss", -157_500),
        WaterfallStep.Delta("External cost", -26_250),
        WaterfallStep.Delta("Internal cost", -13_125),
        WaterfallStep.Total("Technical result", 65_625))
    .WithCurrency("EUR")
    .WithFormat("N0")
    .WithHeight("280px")
```

# Steps: a total is a LEVEL, a delta is a CHANGE

| Kind | `Amount` means | Drawn as |
|---|---|---|
| `WaterfallStepKind.Total` (`"total"`) | the level itself | a bar standing on zero; the running level continues FROM it (a subtotal resets it) |
| `WaterfallStepKind.Delta` (`"delta"`, the default) | the signed change | a bar hanging from the running level, its value printed with its sign |

The caller states which a step is — the control never infers "the last bar must be the sum". The
last step, when it is a total, is the **result** and is toned by its sign (`result-positive` /
`result-negative`); deltas are `increase` / `decrease`, other totals `total`. Both vocabularies are
open string constants. A step whose kind the layout does not know is never given a meaning: it is
left out of the running level, not drawn, and reported in `WaterfallLayout.Unrecognized` (the view
names it under the chart). A blank kind is the default, a delta.

# Geometry is the control's, not the view's

`WaterfallControl.Layout(steps)` is pure and unit-tested (`FigureControlsTest`): it returns every
bar's `From` / `To`, its `BottomPercent` / `HeightPercent` on an axis that spans every level reached
and always includes zero, the `LevelPercent` its connector runs at, and `ZeroPercent` for the
baseline. A present-but-tiny amount keeps a visible sliver, grown from the bar's anchor in the direction of
its sign (a tiny loss stays below zero). When that anchor sits on the axis edge, the axis gains a
hair of headroom on that side rather than clamping the bar away. An exact zero draws nothing. It returns
`null` — and the view says *"Nothing to bridge."* in the viewer's language — for no steps, a
non-finite amount, an axis that overflows the double range, or every level at zero. See
[Authoring for every renderer](/Doc/GUI/CrossRendererAuthoring) for why the arithmetic lives here.

# Binding

`Steps`, `Currency`, `Format` and `Height` are all `object`: hand the rows over directly, or bind
them to a live data section with a `JsonPointerReference` — a template returns at once and the
projection feeds it ([Data Binding](/Doc/GUI/DataBinding)):

```csharp
Controls.Waterfall(new JsonPointerReference("waterfall"))
    .WithCurrency(new JsonPointerReference("currency"))
```

`AnalysisRows.Resolve<WaterfallStep>` reads both forms. Values are formatted with the .NET numeric
`Format` (default `N0`) in the viewer's locale; the Blazor renderer ships in the analysis view pack
(`MeshWeaver.Blazor.Analysis`, `AddAnalysisViews`).

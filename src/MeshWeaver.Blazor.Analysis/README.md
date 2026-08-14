# MeshWeaver.Blazor.Analysis

The analysis view pack: Blazor renderers for the standard analysis controls —
`KpiStripControl` (headline-figure tiles), `TowerControl` (excess-of-loss band stacks over a
retention), and `ComparisonBarsControl` (two bars per measure on one shared scale).

A plain view-pack class library in the sense of the
[UI Extensibility](https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/UiExtensibility.md)
architecture: component types plus one registration entry point, no routable pages, no shell tags.
The control records (and the geometry they resolve server-side) live in `MeshWeaver.Layout`; this
package only projects them.

```csharp
config.AddAnalysisViews();   // register BEFORE AddBlazor()
```

Part of [MeshWeaver](https://github.com/Systemorph/MeshWeaver).

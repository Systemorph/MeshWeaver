# MeshWeaver.Blazor.Radzen

Radzen Blazor renderers for MeshWeaver layout controls, providing a fully open-source (MIT licensed) view pack.

## Overview

This project supplies Blazor views that render MeshWeaver `UiControl`s with [Radzen Blazor](https://github.com/radzenhq/radzen-blazor) components. It contributes two renderers:

| Control (from `MeshWeaver.Layout`) | Radzen view |
|---|---|
| `PivotGridControl` (`MeshWeaver.Layout.Pivot`) | `RadzenPivotGridView` — a cross-tab rendered on `RadzenDataGrid` |
| `ChartControl` (`MeshWeaver.Layout.Chart`) | `RadzenChartView` |

Both views derive from `RadzenViewBase<TControl, TView>`, the Radzen counterpart to the standard `BlazorView` base in `MeshWeaver.Blazor`.

For ordinary tabular data, use the framework's `DataGridControl` (`Controls.DataGrid(...)`) — this package is specifically the Radzen pivot and chart pack.

## Installation

1. Add the project reference to your Blazor application:
```xml
<ProjectReference Include="..\MeshWeaver.Blazor.Radzen\MeshWeaver.Blazor.Radzen.csproj" />
```

2. Register Radzen services in your DI configuration:
```csharp
services.AddRadzenServices();
```

3. Register the views on your MessageHub configuration:
```csharp
config
    .AddRadzenDataGrid()   // PivotGridControl -> RadzenPivotGridView
    .AddRadzenCharts();    // ChartControl     -> RadzenChartView
```

4. Add Radzen CSS to your `App.razor` or layout:
```html
<link rel="stylesheet" href="_content/Radzen.Blazor/css/material-base.css">
```

Or for dark theme:
```html
<link rel="stylesheet" href="_content/Radzen.Blazor/css/material-dark-base.css">
```

### Service Configuration

`AddRadzenServices()` registers:
- Radzen component services (DialogService, NotificationService, TooltipService, ContextMenuService) via `AddRadzenComponents()`
- `DynamicTypeGenerator` — builds the runtime row types the pivot view binds to. It is registered as a **singleton whose memoization cache lives and dies with the ServiceProvider**, never a process-wide static cache (see [NoStaticState.md](../MeshWeaver.Documentation/Data/Architecture/NoStaticState.md)).

## Pivot Rendering

`RadzenPivotGridView` flattens a `PivotGridControl`'s row/column hierarchy into dynamically generated row objects (`DynamicPivotRow` + `DynamicTypeGenerator`), because `RadzenDataGrid` binds to typed properties rather than an untyped cell matrix. Column groups are flattened into individual columns.

## Performance Considerations

- **Large Datasets**: Radzen DataGrid performs well with ~1000-5000 rows. For larger datasets, use server-side paging.
- **Custom Templates**: Complex templates can impact rendering performance.
- **Filtering**: Filtering is client-side by default.

## Theme Support

Radzen provides multiple built-in themes:
- Material (light/dark)
- Standard (light/dark)
- Default (light/dark)
- Fluent (light/dark)

Change theme by referencing different CSS files:
```html
<link rel="stylesheet" href="_content/Radzen.Blazor/css/fluent-base.css">
```

## License

Radzen Blazor is MIT licensed and completely free to use. See [Radzen Blazor GitHub](https://github.com/radzenhq/radzen-blazor) for more information.

## Dependencies

- **MeshWeaver.Blazor** - Base BlazorView infrastructure
- **Radzen.Blazor** - Radzen Blazor components (MIT licensed)

## Related Projects
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - Core layout and UI control system
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Blazor components
- [Radzen Blazor](https://github.com/radzenhq/radzen-blazor) - Open source Blazor components

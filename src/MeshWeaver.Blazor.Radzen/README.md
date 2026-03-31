# MeshWeaver.Blazor.Radzen

A Radzen Blazor DataGrid adapter for MeshWeaver's GridModel, providing a fully open-source (MIT licensed) grid solution.

## Overview

This project provides a Radzen DataGrid component that consumes the same `GridControl` and `GridOptions` models used by the previous AgGrid adapter. Radzen Blazor is completely free and open source under the MIT license.

## Features

- **Pure .NET/Blazor**: Minimal JavaScript, fully managed code
- **Reuses GridModel**: Same `GridOptions`, `ColDef`, and `GridControl` definitions
- **Open Source**: MIT licensed, completely free with no restrictions
- **Responsive Design**: Built-in responsive layout support
- **Column Features**: Sorting, filtering, resizing, hiding
- **Pagination**: Built-in paging support
- **Theming**: Supports Radzen themes including dark mode

## Installation

1. Add the project reference to your Blazor application:
```xml
<ProjectReference Include="..\MeshWeaver.Blazor.Radzen\MeshWeaver.Blazor.Radzen.csproj" />
```

2. Configure Radzen services in your `Program.cs` or startup configuration:
```csharp
// Add Radzen services
builder.Services.AddRadzenServices();
```

3. Register the Radzen DataGrid view in your MessageHub configuration:
```csharp
config.AddRadzenDataGrid();
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

The `AddRadzenServices()` extension method configures:
- Radzen component services (DialogService, NotificationService, TooltipService, ContextMenuService)

## Usage

Use the same `GridControl` and `GridOptions` as you would with AgGrid:

```csharp
var gridControl = new GridControl(
    new GridOptions
    {
        ColumnDefs = new[]
        {
            new ColDef { Field = "name", HeaderName = "Name", Sortable = true },
            new ColDef { Field = "age", HeaderName = "Age", Sortable = true, Filter = true },
            new ColDef { Field = "email", HeaderName = "Email" }
        },
        RowData = new[]
        {
            new { name = "John", age = 30, email = "john@example.com" },
            new { name = "Jane", age = 25, email = "jane@example.com" }
        }
    }
);
```

## GridOptions Support Matrix

| Feature | AgGrid | Radzen | Notes |
|---------|--------|--------|-------|
| **Columns** |
| ColumnDefs | ✅ | ✅ | Fully supported |
| RowData | ✅ | ✅ | Fully supported |
| DefaultColDef | ✅ | ⚠️ | Partial - used for defaults |
| Field | ✅ | ✅ | Fully supported |
| HeaderName | ✅ | ✅ | Fully supported |
| Width/MinWidth/MaxWidth | ✅ | ✅ | Width supported |
| Flex | ✅ | ⚠️ | Mapped to auto width |
| Hide | ✅ | ✅ | Via Visible property |
| Resizable | ✅ | ✅ | Fully supported |
| Sortable | ✅ | ✅ | Fully supported |
| Filter | ✅ | ⚠️ | Simple filter only |
| Pinned | ✅ | ❌ | Not supported |
| **Styling** |
| CellClass | ✅ | ⚠️ | Limited support |
| CellStyle | ✅ | ✅ | Color, background, font-weight |
| HeaderClass | ✅ | ⚠️ | Limited support |
| RowStyle | ✅ | ⚠️ | Partial support |
| RowHeight | ✅ | ⚠️ | Affects page size calculation |
| HeaderHeight | ✅ | ❌ | Not directly supported |
| **Formatting** |
| ValueGetter | ✅ | ❌ | Not supported (JS function) |
| ValueFormatter | ✅ | ⚠️ | Basic numeric formatting only |
| CellRenderer | ✅ | ⚠️ | Use Template instead |
| **Grouping & Aggregation** |
| RowGroup | ✅ | ❌ | Not supported |
| GroupDisplayType | ✅ | ❌ | Not supported |
| AggFunc | ✅ | ❌ | Not supported |
| **Pivot** |
| PivotMode | ✅ | ❌ | Not supported |
| Pivot | ✅ | ❌ | Not supported |
| **Tree Data** |
| TreeData | ✅ | ❌ | Not supported |
| GetDataPath | ✅ | ❌ | Not supported |
| **Column Groups** |
| ColGroupDef | ✅ | ⚠️ | Flattened to columns |
| **Other** |
| Editable | ✅ | ✅ | Supported |
| SideBar | ✅ | ❌ | Not supported |
| DomLayout | ✅ | ⚠️ | autoHeight supported |

## Limitations

### JavaScript Functions
AgGrid supports JavaScript function strings which are **not supported** in Radzen adapter. Consider:
- Using simple field binding instead of ValueGetter
- Using Template for custom rendering
- Pre-formatting data on the server

### Advanced Features Not Supported
- **Grouping**: Row grouping, aggregation functions
- **Pivot Tables**: Pivot mode and pivot columns
- **Tree Data**: Hierarchical data structures
- **Column Pinning**: Left/right pinned columns
- **Master/Detail**: Expandable rows with detail views

### Workarounds

#### Custom Cell Rendering
Use Radzen's Template feature in the component or pre-format data.

#### Value Formatting
Pre-format data on server:

```csharp
var rowData = items.Select(item => new
{
    name = item.Name,
    amount = item.Amount.ToString("C2"),
    date = item.Date.ToString("yyyy-MM-dd")
});
```

#### Column Groups
Column groups are automatically flattened to individual columns.

## Performance Considerations

- **Large Datasets**: Radzen DataGrid performs well with ~1000-5000 rows. For larger datasets, use server-side paging.
- **Custom Templates**: Complex templates can impact rendering performance.
- **Filtering**: Filtering is client-side by default.

## Migration from AgGrid

To migrate from AgGrid to Radzen:

1. Replace `config.AddAgGrid()` with `config.AddRadzenDataGrid()`
2. Replace `services.AddBlazoriseServices()` with `services.AddRadzenServices()`
3. Test grid functionality, especially:
   - Custom cell renderers
   - Value formatters
   - Grouping/aggregation features
4. Adjust GridOptions as needed:
   - Remove unsupported features
   - Pre-format data instead of using JS functions

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
- **MeshWeaver.GridModel** - Grid model definitions
- **Radzen.Blazor** - Radzen Blazor components (MIT licensed)

## Related Projects
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - Core layout and UI control system
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Blazor components
- [Radzen Blazor](https://github.com/radzenhq/radzen-blazor) - Open source Blazor components

# MeshWeaver.GridModel

## Overview
MeshWeaver.GridModel provides the core grid model definitions and controls for the MeshWeaver ecosystem. This library defines the `GridControl` class that inherits from `UiControl<GridControl>` in the MeshWeaver.Layout project, allowing for consistent data grid rendering across different UI implementations.

## Features
- `GridControl` class for UI implementations
- Column definition models for grid configuration
- Data structure models for grid rendering
- Integration with the MeshWeaver UI control system

## Usage
```csharp
// Create a basic grid control
var gridData = new
{
    ColumnDefs = new[]
    {
        new ColDef { Field = "name", HeaderName = "Name" },
        new ColDef { Field = "age", HeaderName = "Age" },
        new ColDef { Field = "country", HeaderName = "Country" }
    },
    RowData = new[]
    {
        new { name = "John", age = 30, country = "USA" },
        new { name = "Sarah", age = 28, country = "Canada" },
        new { name = "Miguel", age = 32, country = "Mexico" }
    }
};

// Create the grid control
var gridControl = new GridControl(gridData);

// Configure the grid (optional)
gridControl = gridControl.WithClass("customer-grid")
                         .WithStyle(style => style.Width("100%").Height("500px"));
```

## Advanced Grid Configuration
```csharp
// Advanced configuration with grid options
var gridData = new
{
    ColumnDefs = new[]
    {
        new ColDef 
        { 
            Field = "name", 
            HeaderName = "Name",
            Filter = true,
            Sortable = true
        },
        new ColDef 
        { 
            Field = "age", 
            HeaderName = "Age",
            Filter = "agNumberColumnFilter",
            Sortable = true 
        },
        new ColDef 
        { 
            Field = "country", 
            HeaderName = "Country",
            Filter = true 
        }
    },
    RowData = GetCustomers(), // Your data source method
    DefaultColDef = new ColDef
    {
        Resizable = true,
        MinWidth = 100
    },
    Pagination = true,
    PaginationPageSize = 10
};

var gridControl = new GridControl(gridData);
```

## Key Concepts
- Grid control architecture
- Column definitions and configuration
- Row data structure
- Grid options and features
- Data binding patterns

## Integration with MeshWeaver
- Extends the MeshWeaver.Layout UiControl system
- Provides models that UI implementations can render
- Works seamlessly with Blazor, React, or other UI technologies

## UI Implementations
This library provides the model definitions and controls, but does not include any UI rendering capabilities. For UI rendering, use one of the following implementations:
- [MeshWeaver.Blazor.Radzen](../MeshWeaver.Blazor.Radzen/README.md) - Blazor implementation using Radzen DataGrid (MIT licensed, fully open source)

## Related Projects
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - Core layout and UI control system
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Blazor components

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.

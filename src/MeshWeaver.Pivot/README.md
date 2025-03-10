# MeshWeaver.Pivot

## Overview
MeshWeaver.Pivot provides powerful data pivoting capabilities that serve as the foundation for creating pivot grids and pivot charts in the MeshWeaver ecosystem. This library defines the core pivot models, builders, and aggregation functions that allow for flexible analysis and visualization of multidimensional data.

## Features
- Flexible `PivotBuilder` for creating pivot tables with rows, columns, and aggregations
- `DataCubePivotBuilder` for working with data cube sources
- Support for various aggregation operations (sum, count, average, etc.)
- Multi-dimensional data slicing and grouping
- Extensible pivot model architecture
- Factory methods for easy pivot creation

## Usage

### Basic Pivot Example
```csharp
// Create a simple pivot with rows, columns and aggregation using PivotFactory
var pivotModel = workspace.Pivot(salesData)
    .GroupByRows(x => x.Region) // Group rows by Region
    .GroupByColumns(x => x.Product) // Group columns by Product
    .Aggregate(x => x.Amount) // Aggregate by Amount
    .Build();

// Convert to a MeshWeaver GridControl for display
var gridControl = new GridControl(pivotModel);
```

### Count Aggregation Example
```csharp
// Create a pivot that counts occurrences
var pivotModel = workspace.Pivot(salesData)
    .GroupByRows(x => x.Region) 
    .GroupByColumns(x => x.Product)
    .Count() // Count records in each group
    .Build();
```

### Multiple Dimensions Example
```csharp
// Group by multiple dimensions
var pivotModel = workspace.Pivot(salesData)
    .GroupByRows(x => x.Region)
    .ThenBy(x => x.Quarter) // Additional row grouping
    .GroupByColumns(x => x.Product)
    .ThenBy(x => x.Category) // Additional column grouping
    .Aggregate(x => x.Amount)
    .Build();
```

### Working with DataCubes
```csharp
// Create a pivot from data cubes
var pivotModel = workspace.ForDataCubes(dataCubes)
    .GroupByRows(x => x.Region)
    .GroupByColumns(x => x.Product)
    .Aggregate(x => x.Amount)
    .Build();
```

### Working with a Single DataCube
```csharp
// Create a pivot from a single data cube
var pivotModel = workspace.Pivot(salesCube)
    .GroupByRows(x => x.Region)
    .GroupByColumns(x => x.Product)
    .Aggregate(x => x.Amount)
    .Build();
```

### Average Aggregation with DataCubes
```csharp
// Calculate averages in pivot
var pivotModel = workspace.ForDataCubes(dataCubes)
    .GroupByRows(x => x.Region)
    .GroupByColumns(x => x.Product)
    .Average(x => x.Amount) // Calculate average amount
    .Build();
```

## Visualization Options
The pivot models created with MeshWeaver.Pivot can be visualized in various ways:

### As Grid
```csharp
// Create a grid from pivot model
var pivotGrid = new GridControl(pivotModel);
```

### As Chart
```csharp
// Create a chart from pivot model
var chartModel = ChartModel
    .Bar()
    .WithPivotData(pivotModel);
    
var pivotChart = new ChartControl(chartModel);
```

## Key Concepts
- Pivot builders and fluent API
- PivotFactory extension methods on IWorkspace
- Dimension grouping (rows and columns)
- Aggregation functions (sum, count, average)
- Data representation models
- Hierarchical data structures

## Integration with MeshWeaver
- Provides data models for MeshWeaver.GridModel grids
- Provides data for MeshWeaver.Charting visualizations
- Works seamlessly with MeshWeaver.Data and data cube components

## Related Projects
- [MeshWeaver.GridModel](../MeshWeaver.GridModel/README.md) - For displaying pivot data in grids
- [MeshWeaver.Charting](../MeshWeaver.Charting/README.md) - For visualizing pivot data in charts
- [MeshWeaver.Blazor.AgGrid](../MeshWeaver.Blazor.AgGrid/README.md) - Blazor implementation of pivot grids
- [MeshWeaver.Blazor.ChartJs](../MeshWeaver.Blazor.ChartJs/README.md) - Blazor implementation of pivot charts

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.

# MeshWeaver.Charting.Pivot

## Overview
MeshWeaver.Charting.Pivot bridges the gap between MeshWeaver.Pivot and MeshWeaver.Charting, providing specialized functionality for visualizing pivot data in charts. This library enables seamless transformation of pivot models into chart visualizations, supporting various chart types and aggregation views.

## Features
- Direct conversion of pivot models to chart models
- Specialized chart builders for pivot data
- Automatic axis and series generation from pivot structure
- Support for all chart types (Bar, Line, Radar, Waterfall, etc.)
- Aggregation visualization options
- Row and column dimension mapping to chart elements
- Extensive chart customization options

## Usage

### Basic Bar Chart
```csharp
// Create a bar chart with rows by Name and columns by ValueIndex
var chart = workspace.Pivot(dataCube)
    .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
    .SliceRowsBy(nameof(Name))
    .ToBarChart()
    .WithTitle("Aggregated by Name");
```

### Stacked Bar Chart with Custom Labels
```csharp
// Create a stacked bar chart with multiple dimensions and custom label settings
var chart = workspace.Pivot(dataCube)
    .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
    .SliceRowsBy(nameof(Name), nameof(Country))
    .ToBarChart(chart => 
        chart.AsStackedWithScatterTotals()
            .WithOptions(m => m.WithLabelsFromLevels(0, 1))
    );
```

### Line Chart with Smoothing
```csharp
// Create a line chart with smoothed lines and custom options
var chart = workspace.Pivot(dataCube)
    .SliceRowsBy(nameof(Name), nameof(Country))
    .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
    .ToLineChart(chart =>
        chart.WithSmoothedLines("Paolo.Italy")
            .WithSmoothedLines(new Dictionary<string, double>() { { "Alessandro.Italy", 0.5 } })
            .WithRows("Alessandro.Italy", "Paolo.Italy")
            .WithOptions(model => model.WithLabels("8", "9", "10", "11", "12", "13", "14"))
    );
```

### Radar Chart with Styling
```csharp
// Create a radar chart with filled areas and custom styling
var chart = workspace.Pivot(dataCube)
    .SliceRowsBy(nameof(Name), nameof(Country))
    .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
    .ToRadarChart(chart =>
        chart.WithSmoothedLines(new Dictionary<string, double>() { { "Alessandro.Italy", 0.2 } })
            .WithFilledArea()
            .WithRows("Alessandro.Italy", "Paolo.Italy")
            .WithColorScheme(new string[] { "#1ECBE1", "#E1341E" })
            .WithTitle("Two lines radar plot", t => t.WithFontSize(15).AlignAtStart())
    );
```

### Waterfall Chart with Custom Colors
```csharp
// Create a waterfall chart with custom colors and styling
var chart = workspace.Pivot(dataCube)
    .SliceColumnsBy(nameof(Country), nameof(Name))
    .ToWaterfallChart(chart =>
        chart.WithStylingOptions(b =>
            b.WithIncrementColor("#08BFD1")
                .WithDecrementColor("#01AB6C")
                .WithTotalColor("#A7E1ED")
                .WithLabelsFontColor("white")
                .WithLabelsFontSize(14)
        )
        .WithTotals(col => col.IsTotalForSlice(nameof(Country)))
    );
```

### Mixed Chart Types
```csharp
// Create a mixed chart with both bars and lines
var chart = workspace.Pivot(dataCube)
    .SliceRowsBy(nameof(Name))
    .SliceColumnsBy(nameof(RecordWithValues.ValueIndex))
    .ToBarChart(chart => chart.WithRowsAsLine("Paolo"));
```

## Chart Types and Mappings
Different pivot structures can be mapped to various chart types:
- Bar Charts: Standard, Stacked, or Mixed with lines
- Line Charts: Standard or Smoothed lines
- Radar Charts: With or without filled areas
- Waterfall Charts: With custom increment/decrement styling
- Mixed Charts: Combining different visualization types

## Key Concepts
- Pivot to chart model conversion
- Dimension slicing (rows and columns)
- Chart type selection and customization
- Series styling and color schemes
- Advanced chart options (smoothing, stacking, etc.)

## Integration with MeshWeaver
- Seamless integration with MeshWeaver.Pivot models
- Direct conversion to MeshWeaver.Charting models
- Support for all MeshWeaver.Charting visualization types
- Compatible with MeshWeaver.Blazor.ChartJs rendering

## Related Projects
- [MeshWeaver.Pivot](../MeshWeaver.Pivot/README.md) - Core pivot functionality
- [MeshWeaver.Charting](../MeshWeaver.Charting/README.md) - Chart model definitions
- [MeshWeaver.Blazor.ChartJs](../MeshWeaver.Blazor.ChartJs/README.md) - Chart rendering implementation

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.

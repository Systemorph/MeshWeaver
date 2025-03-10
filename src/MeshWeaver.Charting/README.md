# MeshWeaver.Charting

## Overview
MeshWeaver.Charting provides the core chart model definitions and controls for the MeshWeaver ecosystem. This library defines the `ChartControl` class that inherits from `UiControl<ChartControl>` in the MeshWeaver.Layout project, allowing for consistent chart rendering across different UI implementations.

## Features
- `ChartControl` class for UI implementations
- Chart model definitions for various chart types (Bar, Line, Pie, Doughnut, Radar, Polar, Bubble, Scatter, Area)
- Fluent builders for easy chart creation
- Data structure models for chart configuration
- Integration with the MeshWeaver UI control system

## Usage

### Basic Chart Creation
```csharp
// Create a simple bar chart with fluent API
var chartModel = ChartModel
    .Bar()
    .WithData(data1, data2)
    .WithLabels("One", "Two", "Three", "Four")
    .WithLegend("First Dataset", "Second Dataset");

// Create the chart control
var chartControl = new ChartControl(chartModel);
```

### Advanced Chart Examples

#### Bar Chart with Styling
```csharp
var chart = ChartModel
    .Bar()
    .WithData(data1, data2)
    .WithLabels(labels)
    .WithLegend("Dataset 1", "Dataset 2")
    .WithPalette(ColorSchemes.Default);
    
var chartControl = new ChartControl(chart)
    .WithClass("sales-chart")
    .WithStyle(style => style.Width("100%").Height("300px"));
```

#### Line Chart
```csharp
var chart = ChartModel
    .Line()
    .WithData(data1, data2)
    .WithLabels(labels)
    .WithLegend("First Dataset", "Second Dataset");
    
var chartControl = new ChartControl(chart);
```

#### Time Series Chart
```csharp
var chart = ChartModel
    .Line()
    .WithTimeData(dates, data1, data2)
    .WithLegend("First Series", "Second Series");

var chartControl = new ChartControl(chart);
```

#### Stacked Bar Chart
```csharp
var chart = ChartModel
    .Bar()
    .WithData(data1, data2, data3)
    .WithLabels(labels)
    .WithLegend("First", "Second", "Third")
    .Stacked();

var chartControl = new ChartControl(chart);
```

#### Floating Bar Chart
```csharp
var chart = ChartModel
    .FloatingBar()
    .WithDataRanges(x1, x2)
    .WithLabels(labels);

var chartControl = new ChartControl(chart);
```

#### Bubble Chart
```csharp
var chart = ChartModel
    .Bubble()
    .WithDataPoints(x1, y, data1)
    .WithLabels(labels);

var chartControl = new ChartControl(chart);
```

#### Pie/Doughnut Chart
```csharp
var chart = ChartModel
    .Pie()
    .WithData(data1)
    .WithLabels(labels);

// Or for a doughnut chart
var doughnutChart = ChartModel
    .Doughnut()
    .WithData(data1)
    .WithLabels(labels);

var chartControl = new ChartControl(chart);
```

#### Area Chart
```csharp
var chart = ChartModel
    .Area()
    .WithData(data1, data2)
    .WithLabels(labels)
    .WithLegend("Dataset 1", "Dataset 2");

var chartControl = new ChartControl(chart);
```

#### Quick Chart Creation
```csharp
// Quick chart creation with minimal configuration
var chart = ChartModel.QuickDraw(ChartType.Bar, data1);

var chartControl = new ChartControl(chart);
```

## Chart Types
MeshWeaver.Charting supports a wide range of chart types:
- Bar Charts (standard, stacked, horizontal, floating, waterfall)
- Line Charts (standard, area, scatter)
- Pie and Doughnut Charts
- Radar and Polar Charts
- Bubble Charts
- Scatter Charts

## Key Concepts
- Chart control architecture
- Fluent builders for easy chart creation
- Chart model definitions
- Chart type specifications
- Data structure models

## Integration with MeshWeaver
- Extends the MeshWeaver.Layout UiControl system
- Provides models that UI implementations can render
- Works seamlessly with Blazor, React, or other UI technologies

## UI Implementations
This library provides the model definitions and controls, but does not include any UI rendering capabilities. For UI rendering, use one of the following implementations:
- [MeshWeaver.Blazor.ChartJs](../MeshWeaver.Blazor.ChartJs/README.md) - Blazor implementation using Chart.js

## Related Projects
- [MeshWeaver.Layout](../MeshWeaver.Layout/README.md) - Core layout and UI control system
- [MeshWeaver.Blazor](../MeshWeaver.Blazor/README.md) - Blazor components

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.

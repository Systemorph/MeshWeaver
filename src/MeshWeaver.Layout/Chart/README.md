# MeshWeaver Chart Controls API

An improved charting API for MeshWeaver that provides a more intuitive and consistent way to create charts.

## Key Improvements Over Old API

### 1. **Clearer Chart Construction**

**Old API:**
```csharp
// Confusing - need to use Chart.Create() for multiple series
var chart = Chart.Create(DataSet.Bar(data1, "First"), DataSet.Bar(data2, "Second"))
    .WithLabels(labels)
    .WithLegend()  // Must explicitly add legend or nothing shows!
    .WithTitle("My Chart");
```

**New API:**
```csharp
// Clear and intuitive - chart is a container for series
var chart = Charts.Create()
    .WithSeries(new BarSeries(data1, "First"))
    .WithSeries(new BarSeries(data2, "Second"))
    .WithLabels(labels)
    .WithTitle("My Chart");
    // Legend is automatically shown for multiple labeled series!
```

### 2. **Series Types Are Explicit**

**Old API:**
```csharp
// Not obvious what creates what type of chart
var chart = Chart.Create(
    DataSet.Line(data1, "First"),
    DataSet.Bar(data2, "Second")
);
```

**New API:**
```csharp
// Crystal clear - you're creating specific series types
var chart = Charts.Create()
    .WithSeries(new LineSeries(data1, "First"))
    .WithSeries(new BarSeries(data2, "Second"));
```

### 3. **Automatic Legend Management**

**Old API:**
```csharp
// Forgetting .WithLegend() means no legend appears
var chart = Chart.Create(DataSet.Bar(data1, "First"), DataSet.Bar(data2, "Second"))
    .WithLabels(labels)
    .WithLegend()  // REQUIRED or legend doesn't show!
    .WithTitle("Bar Chart");
```

**New API:**
```csharp
// Legend automatically appears for multiple series with labels
var chart = Charts.Create()
    .WithSeries(new BarSeries(data1, "First"))
    .WithSeries(new BarSeries(data2, "Second"))
    .WithLabels(labels)
    .WithTitle("Bar Chart");
    // Legend appears automatically!
```

### 4. **Better Type Safety**

**Old API:**
```csharp
// Line dataset with area fill - must know WithArea() method exists
var chart = Chart.Create(
    new LineDataSet(data1).WithArea(),
    new LineDataSet(data2).WithArea()
);
```

**New API:**
```csharp
// Clear, discoverable API
var chart = Charts.Create()
    .WithSeries(new LineSeries(data1, "First").WithFill())
    .WithSeries(new LineSeries(data2, "Second").WithFill());
```

### 5. **Consistent Naming**

**Old API:**
```csharp
Chart.Bar(data)           // Static methods on Chart class
Chart.Create(datasets)    // Different pattern for multiple series
new BarDataSet(data)      // Or use constructors directly
```

**New API:**
```csharp
Charts.Bar(data)                          // Simple charts via helper
Charts.Create().WithSeries(...)           // Explicit multi-series
new ChartControl().WithSeries(...)        // Direct control instantiation
```

## Usage Examples

### Simple Bar Chart
```csharp
var chart = Charts.Bar(data)
    .WithLabels(labels)
    .WithTitle("Sales by Quarter");
```

### Multi-Series Line Chart
```csharp
var chart = Charts.Create()
    .WithSeries(new LineSeries(revenue, "Revenue"))
    .WithSeries(new LineSeries(costs, "Costs"))
    .WithLabels(months)
    .WithTitle("Financial Overview");
```

### Stacked Bar Chart
```csharp
var chart = Charts.Create()
    .WithSeries(new BarSeries(data1, "Product A"))
    .WithSeries(new BarSeries(data2, "Product B"))
    .WithLabels(quarters)
    .Stacked()
    .WithTitle("Quarterly Sales");
```

### Area Chart
```csharp
var chart = Charts.Create()
    .WithSeries(new LineSeries(data1, "Series 1").WithFill())
    .WithSeries(new LineSeries(data2, "Series 2").WithFill())
    .WithLabels(labels);
```

### Scatter Plot
```csharp
var chart = Charts.Scatter(points, "Dataset")
    .WithTitle("Correlation Analysis");
```

### Pie Chart
```csharp
var chart = Charts.Pie(marketShares)
    .WithLabels(companies)
    .WithTitle("Market Share");
```

## Available Series Types

- **BarSeries** - Vertical bar charts
- **LineSeries** - Line charts (with optional fill for area charts)
- **PieSeries** - Pie charts
- **DoughnutSeries** - Doughnut charts
- **RadarSeries** - Radar/spider charts
- **PolarSeries** - Polar area charts
- **ScatterSeries** - Scatter plots
- **BubbleSeries** - Bubble charts

## Configuration Options

### Chart-Level
- `WithTitle(string)` - Set chart title
- `WithSubtitle(string)` - Set subtitle
- `WithLabels(string[])` - Set category labels
- `WithLegend(bool)` - Manually control legend
- `WithLegendPosition(position)` - Position the legend
- `Stacked()` - Make chart stacked
- `WithoutAnimation()` - Disable animations
- `WithWidth(size)` / `WithHeight(size)` - Set dimensions

### Series-Level
- `WithLabel(string)` - Set series label
- `WithBackgroundColor(color)` - Set fill color
- `WithBorderColor(color)` - Set border color
- `WithBorderWidth(width)` - Set border width
- `WithHidden(bool)` - Hide series initially

### Series-Specific
**LineSeries:**
- `WithTension(double)` - Smooth curved lines
- `WithFill(bool)` - Create area chart
- `WithPointRadius(double)` - Point size

**BarSeries:**
- `WithBarPercentage(double)` - Bar thickness
- `WithCategoryPercentage(double)` - Category spacing

**DoughnutSeries:**
- `WithCutout(double)` - Center hole size

## Migration Guide

### From Old API to New API

**Single Series Charts:**
```csharp
// Old
var chart = Chart.Bar(data, "Label");

// New
var chart = Charts.Bar(data, "Label");  // Same!
```

**Multiple Series:**
```csharp
// Old
var chart = Chart.Create(
    DataSet.Bar(data1, "First"),
    DataSet.Bar(data2, "Second")
).WithLegend();

// New
var chart = Charts.Create()
    .WithSeries(new BarSeries(data1, "First"))
    .WithSeries(new BarSeries(data2, "Second"));
```

**Mixed Charts:**
```csharp
// Old
var chart = Chart.Create(
    DataSet.Line(data1, "Line"),
    DataSet.Bar(data2, "Bar")
).WithLegend();

// New
var chart = Charts.Create()
    .WithSeries(new LineSeries(data1, "Line"))
    .WithSeries(new BarSeries(data2, "Bar"));
```

## Benefits Summary

1. ✅ **More Intuitive** - Clear what you're creating
2. ✅ **Better Defaults** - Auto-legend when appropriate
3. ✅ **Type Safe** - Strong typing for series-specific options
4. ✅ **Consistent** - Same pattern for all chart types
5. ✅ **Discoverable** - IntelliSense-friendly API
6. ✅ **Flexible** - Easy to mix chart types
7. ✅ **Clean** - Fluent API throughout
8. ✅ **Radzen Ready** - View adapters included for Radzen Blazor

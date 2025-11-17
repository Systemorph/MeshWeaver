# Pivot to Chart Migration Guide

This guide shows how to migrate from the old `.Pivot()` + `ToDataCube()` API to the new GroupBy-based Chart extensions.

## Key Differences

### Old API (Pivot + DataCube)
- Required `ToDataCube()` conversion
- Used `SliceRowsBy()` and `SliceColumnsBy()`
- Used `WithAggregation()`
- Required data cube infrastructure

### New API (GroupBy + Charts)
- Works directly with `IEnumerable<T>`
- Uses standard LINQ `GroupBy()`
- Direct conversion to chart controls
- No data cube dependencies

## Migration Examples

### Example 1: Simple Bar Chart by Category

**Old Code:**
```csharp
layoutArea.Workspace
    .Pivot(data.ToDataCube())
    .SliceColumnsBy(nameof(Category))
    .ToBarChart(builder => builder
        .WithOptions(o => o.OrderByValueDescending()))
    .Select(chart => chart.ToControl())
```

**New Code:**
```csharp
Observable.Return(
    data.ToBarChart(
        keySelector: x => x.Category,
        valueSelector: g => g.Sum(x => x.Amount),
        orderByValueDescending: true
    )
    .WithTitle("Sales by Category")
)
```

### Example 2: Line Chart with Multiple Series (Revenue by Month per Year)

**Old Code:**
```csharp
layoutArea.Workspace
    .Pivot(data.ToDataCube())
    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
    .SliceRowsBy(nameof(NorthwindDataCube.OrderYear))
    .ToLineChart(builder => builder)
    .Select(chart => chart.ToControl())
```

**New Code:**
```csharp
Observable.Return(
    data.ToLineChart(
        rowKeySelector: x => x.OrderDate.Year,
        colKeySelector: x => x.OrderDate.ToString("MMM"),
        valueSelector: g => g.Sum(x => x.Amount),
        rowLabelSelector: year => year.ToString(),
        colLabelSelector: month => month
    )
    .WithTitle("Revenue Summary by Year")
)
```

### Example 3: Pie Chart by Category

**Old Code:**
```csharp
var categoryData = data.GroupBy(x => x.Category)
    .Select(g => new { Category = g.Key, Revenue = g.Sum(x => x.Amount) })
    .ToArray();

var chart = Chart.Pie((IEnumerable)categoryData.Select(c => c.Revenue), "Revenue")
    .WithLabels(categoryData.Select(c => c.Category));
```

**New Code:**
```csharp
var chart = data.ToPieChart(
    keySelector: x => x.Category,
    valueSelector: g => g.Sum(x => x.Amount),
    labelSelector: category => categories[category].CategoryName
);
```

### Example 4: Stacked Bar Chart (Discount Impact by Category)

**Old Code:**
```csharp
// Manual grouping and data transformation
var discountData = data.GroupBy(x => new { x.CategoryName, x.DiscountBracket })
    .Select(g => new { g.Key.CategoryName, g.Key.DiscountBracket, Amount = g.Sum(x => x.Amount) })
    .ToList();

var dataSets = discountBrackets.Select(bracket => {
    var amounts = categoryNames.Select(category =>
        discountData.FirstOrDefault(x => x.CategoryName == category && x.DiscountBracket == bracket)?.Amount ?? 0
    ).ToArray();
    return new BarDataSet(amounts).WithLabel(bracket);
}).ToArray();

var chart = Chart.Create(dataSets).Stacked().WithLabels(categoryNames);
```

**New Code:**
```csharp
var chart = data.ToStackedBarChart(
    rowKeySelector: x => x.DiscountBracket,
    colKeySelector: x => x.CategoryName,
    valueSelector: g => g.Sum(x => x.Amount),
    rowLabelSelector: bracket => bracket,
    colLabelSelector: category => category
);
```

### Example 5: Top Products Performance Trends

**Old Code:**
```csharp
var topProducts = data.GroupBy(x => x.Product)
    .OrderByDescending(g => g.Sum(x => x.Amount))
    .Take(5)
    .Select(g => g.Key)
    .ToHashSet();

var filteredData = data.Where(x => topProducts.Contains(x.Product));

return layoutArea.Workspace
    .Pivot(filteredData.ToDataCube())
    .WithAggregation(a => a.Sum(x => x.Amount))
    .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
    .SliceRowsBy(nameof(NorthwindDataCube.Product))
    .ToLineChart(builder => builder)
    .Select(chart => chart.ToControl());
```

**New Code:**
```csharp
var topProducts = data.GroupBy(x => x.Product)
    .OrderByDescending(g => g.Sum(x => x.Amount))
    .Take(5)
    .Select(g => g.Key)
    .ToHashSet();

var filteredData = data.Where(x => topProducts.Contains(x.Product));

return Observable.Return(
    filteredData.ToLineChart(
        rowKeySelector: x => x.Product,
        colKeySelector: x => x.OrderDate.ToString("MMM yyyy"),
        valueSelector: g => g.Sum(x => x.Amount),
        rowLabelSelector: product => product,
        colLabelSelector: month => month
    )
    .WithTitle("Top 5 Products Performance Trends")
);
```

## Common Patterns

### Aggregation Functions

Use standard LINQ aggregation methods for the valueSelector:

**Sum:**
```csharp
valueSelector: g => g.Sum(x => x.Amount)
```

**Average:**
```csharp
valueSelector: g => g.Average(x => x.UnitPrice)
```

**Count:**
```csharp
valueSelector: g => g.Count()
```

**Custom:**
```csharp
valueSelector: g => g.GroupBy(x => x.OrderMonth).Average(monthGroup => monthGroup.Sum(x => x.Quantity))
```

**Note:** The new API uses standard LINQ aggregation methods (Sum, Average, Count, etc.). Type conversions to double happen automatically.

### Label Selectors

**Direct:**
```csharp
labelSelector: x => x.ToString()
```

**With Dictionary Lookup:**
```csharp
labelSelector: categoryId => categories[categoryId].CategoryName
```

**With Formatting:**
```csharp
colLabelSelector: date => date.ToString("MMM yyyy")
```

### Ordering

**Descending by Value:**
```csharp
data.ToBarChart(
    keySelector: x => x.Category,
    valueSelector: g => g.Sum(x => x.Amount),
    orderByValueDescending: true  // Automatic descending sort
)
```

**Custom Ordering:**
```csharp
data.OrderBy(x => x.SomeProperty)
    .ToBarChart(
        keySelector: x => x.Category,
        valueSelector: g => g.Sum(x => x.Amount)
    )
```

## Benefits of New API

1. **Simpler** - No data cube conversion required
2. **Type-Safe** - Full IntelliSense support
3. **Familiar** - Uses standard LINQ patterns
4. **Flexible** - Easy to customize aggregations
5. **Performant** - Direct GroupBy without intermediate structures
6. **Data Binding Ready** - All properties are `object?` for binding support

## Complete Example: ProductAnalysisArea Migration

### Before:
```csharp
public IObservable<UiControl> ProductPerformanceTrends(RenderingContext context)
    => layoutArea.GetDataCube()
        .SelectMany(data =>
        {
            var topProducts = data.GroupBy(x => x.Product)
                .OrderByDescending(g => g.Sum(x => x.Amount))
                .Take(5)
                .Select(g => g.Key)
                .ToHashSet();

            var filteredData = data.Where(x => topProducts.Contains(x.Product));

            return layoutArea.Workspace
                .Pivot(filteredData.ToDataCube())
                .WithAggregation(a => a.Sum(x => x.Amount))
                .SliceColumnsBy(nameof(NorthwindDataCube.OrderMonth))
                .SliceRowsBy(nameof(NorthwindDataCube.Product))
                .ToLineChart(builder => builder)
                .Select(chart => (UiControl)Controls.Stack
                    .WithView(Controls.H2("Top 5 Products Performance Trends"))
                    .WithView(chart.ToControl()));
        });
```

### After:
```csharp
public IObservable<UiControl> ProductPerformanceTrends(RenderingContext context)
    => layoutArea.GetDataCube()
        .Select(data =>
        {
            var topProducts = data.GroupBy(x => x.Product)
                .OrderByDescending(g => g.Sum(x => x.Amount))
                .Take(5)
                .Select(g => g.Key)
                .ToHashSet();

            var filteredData = data.Where(x => topProducts.Contains(x.Product));

            var chart = filteredData.ToLineChart(
                rowKeySelector: x => x.Product,
                colKeySelector: x => x.OrderMonth,
                valueSelector: g => g.Sum(x => x.Amount),
                rowLabelSelector: product => product,
                colLabelSelector: month => month
            ).WithTitle("Top 5 Products Performance Trends");

            return (UiControl)Controls.Stack
                .WithView(Controls.H2("Top 5 Products Performance Trends"))
                .WithView(chart);
        });
```

**Key Changes:**
- `SelectMany` → `Select` (no async pivot operation)
- Removed `Pivot()` and `ToDataCube()`
- Removed `SliceColumnsBy()` and `SliceRowsBy()`
- Direct `ToLineChart()` with GroupBy parameters
- No need for `chart.ToControl()` - chart is already a `UiControl`

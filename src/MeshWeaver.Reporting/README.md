# MeshWeaver.Reporting

## Overview
MeshWeaver.Reporting is a specialized component of the MeshWeaver ecosystem that provides advanced table reporting and formatting capabilities. Built on top of MeshWeaver.Pivot, it extends the core data manipulation features with presentation-layer functionality for creating rich, formatted reports and visualizations.

## Features
- Advanced table reporting with flexible row and column grouping
- Chart generation and visualization capabilities
- Rich formatting options for cells, rows, and columns
- Customizable display names and highlighting
- Aggregation control and visibility management
- Integration with MeshWeaver's data cube system
- Support for hierarchical data structures

## Core Concepts
### Data Flow
```
Raw Data → PivotBuilder → ReportBuilder → Output (Tables/Charts)
```

### Components
- **PivotBuilder**: Handles data transformation and grouping (from MeshWeaver.Pivot)
- **ReportBuilder**: Adds formatting and presentation capabilities
- **DataCubeReportBuilder**: Specialized support for data cube operations

### Output Types
#### Tables
- Pivot tables with multi-dimensional grouping
- Formatted data grids
- Hierarchical data views

#### Charts
- Data visualizations based on pivot results
- Support for various chart types
- Customizable chart formatting

## Relationship with MeshWeaver.Pivot
MeshWeaver.Reporting builds on top of MeshWeaver.Pivot's core functionality:
- **Pivot**: Provides data transformation, grouping, and aggregation ([see MeshWeaver.Pivot documentation](../MeshWeaver.Pivot/README.md))
  - Data source management
  - Dimension handling
  - Aggregation logic
- **Reporting**: Adds presentation and visualization layer
  - Formatting and styling
  - Table and chart generation
  - Visual customization

## Usage Examples
Here are actual examples from our test suite:

### Table Creation
```csharp
// Basic grouping example
var report = pivotBuilder
    .GroupColumnsBy(y => y.LineOfBusiness)
    .GroupRowsBy(y => y.AmountType)
    .ToTable();
```

### Advanced Formatting
```csharp
// Example with column and row formatting
var report = pivotBuilder
    .GroupColumnsBy(y => y.LineOfBusiness)
    .GroupRowsBy(y => y.AmountType)
    .ToTable()
    .WithOptions(rm => rm
        .WithColumns(cols =>
            cols.Modify(
                "Value",
                c => c.WithDisplayName("Amount").Highlighted()
            )
        )
        .WithRows(rows =>
            rows.Modify(
                r => r.RowGroup.DisplayName == "Premium",
                r => r.WithDisplayName("Total Premium").AsTotal()
            )
        )
    );
```

### Aggregation Control
```csharp
// Example of hiding aggregations for specific dimensions
var report = pivotBuilder
    .GroupColumnsBy(y => y.Split)
    .GroupRowsBy(y => y.AmountType)
    .GroupRowsBy(y => y.LineOfBusiness)
    .ToTable()
    .WithOptions(rm => rm.HideRowValuesForDimension("AmountType"));
```

## Supported Data Types
The reporting system works with various domain entities including:
- LineOfBusiness
- Country
- AmountType
- Scenario
- Split
- Currency
- CashflowElement

## Key Features

### Grouping (via Pivot)
- Multi-level row and column grouping
- Flexible grouping criteria
- Support for hierarchical data structures

### Formatting
- Custom display names
- Cell highlighting
- Total row formatting
- Column customization
- Conditional formatting

### Aggregation (via Pivot)
- Configurable aggregation functions
- Selective aggregation visibility
- Multi-level aggregation support

### Visualization
- Table generation
- Chart creation
- Custom formatting options
- Interactive elements

## Integration with MeshWeaver Ecosystem

### Data Cubes
- Seamless integration with MeshWeaver's data cube system
- Support for multi-dimensional data analysis
- Efficient handling of large datasets

### Domain Concepts
Built-in support for common business dimensions:
- Line of Business
- Amount Types
- Currencies
- Scenarios
- Splits
- Custom dimensions

## Best Practices
1. Use appropriate grouping levels for clarity
2. Apply consistent formatting patterns
3. Consider performance with large datasets
4. Leverage built-in aggregation controls
5. Use meaningful display names
6. Choose appropriate visualization types for your data

## Related Projects
- MeshWeaver.Pivot: Core data transformation engine
- MeshWeaver.DataCubes: Multi-dimensional data storage
- MeshWeaver.Core: Base functionality and utilities

## See Also
- [MeshWeaver.Pivot Documentation](../MeshWeaver.Pivot/README.md) for core data transformation features
- [Main MeshWeaver Documentation](../../Readme.md) for overall project architecture

# MeshWeaver.Arithmetics

MeshWeaver.Arithmetics implements the aggregation engine that processes data structures decorated with attributes from MeshWeaver.Arithmetics.Abstractions. It provides runtime aggregation, multiplication, and data transformation capabilities.

## Overview

The library provides:
- Runtime analysis of attribute-decorated classes
- Aggregation pipeline execution
- Multiplication processing
- Extensible aggregation strategies

## Usage

### Basic Aggregation

```csharp
// Define data structure with attributes
public class SalesData
{
    [AggregateBy]
    public string Region { get; set; }
    
    [AggregateOver]
    public string Product { get; set; }
    
    public decimal Amount { get; set; }
}

// Perform aggregation
var aggregator = new Aggregator();
var salesData = new List<SalesData> 
{
    new() { Region = "North", Product = "A", Amount = 100 },
    new() { Region = "North", Product = "A", Amount = 200 },
    new() { Region = "South", Product = "B", Amount = 150 }
};

var results = aggregator.Aggregate(salesData);
// Results grouped by Region and Product with summed Amount
```


## Related Projects

- [MeshWeaver.Arithmetics.Abstractions](../MeshWeaver.Arithmetics.Abstractions/README.md) - Attribute definitions

## Key Concepts
- Arithmetics architecture
- Integration patterns
- Extension points
- Configuration options

## Integration with MeshWeaver
- Works with other MeshWeaver components
- Extends core functionality
- Adds specialized capabilities

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.

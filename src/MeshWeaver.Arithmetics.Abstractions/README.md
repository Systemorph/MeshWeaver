# MeshWeaver.Arithmetics.Abstractions

MeshWeaver.Arithmetics.Abstractions provides the API for defining how data should be aggregated and multiplied in data structures through attributes. This library works in conjunction with the MeshWeaver.Arithmetics implementation to enable declarative aggregation behaviors.

## Overview

The library provides attributes to control:
- Which properties to aggregate by (grouping)
- Which properties to aggregate over (values)
- How properties should be multiplied
- Custom aggregation behaviors

## Usage

### Basic Aggregation

```csharp
public class SalesData
{
    [AggregateBy] // Group by this property
    public string Region { get; set; }
    
    [AggregateOver] // Group by this property too
    public string Product { get; set; }
    
    public decimal Amount { get; set; }
}
```


```

## Related Projects

- MeshWeaver.Arithmetics - Implementation of the aggregation engine

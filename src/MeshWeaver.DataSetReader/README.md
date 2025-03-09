# MeshWeaver.DataSetReader

MeshWeaver.DataSetReader is a foundational library that provides base classes and abstractions for implementing data readers in the MeshWeaver ecosystem. It serves as the core framework for creating specialized data readers for various file formats.

## Overview

The library provides:
- Base classes for implementing data readers
- Common abstractions and interfaces
- Utility functions for data reading operations
- Integration with MeshWeaver.DataStructures

## Architecture

### Core Components

#### Base Classes
- Abstract reader implementations
- Format-specific base classes
- Common utility functions
- Error handling infrastructure

#### Interfaces
- Reader interfaces
- Factory interfaces
- Stream handling interfaces
- Configuration interfaces

## Supported Implementations

The framework supports multiple reader implementations, including:

### Excel Readers
- Binary format (.xls)
- OpenXML format (.xlsx)
- Custom properties support
- Multi-sheet handling

### CSV Reader
- Standard CSV format
- Custom delimiters
- Header row handling
- Data type inference

## Usage Examples

### Implementing a Custom Reader

```csharp
public class CustomFormatReader : DataSetReaderBase
{
    protected override IDataSet ReadDataSetFromStream(Stream stream)
    {
        var dataSet = new DataSet();
        var table = dataSet.Tables.Add("Data");
        
        // Implement format-specific reading logic
        using (var reader = new StreamReader(stream))
        {
            // Read data and populate table
            // ...
        }
        
        return dataSet;
    }
}
```

### Using Reader Factory Pattern

```csharp
public class CustomReaderFactory : IDataSetReaderFactory
{
    public IDataSetReader CreateReader(string format)
    {
        return format.ToLower() switch
        {
            "custom" => new CustomFormatReader(),
            _ => throw new NotSupportedException($"Format {format} not supported")
        };
    }
}
```

### Reading Data

```csharp
public async Task<IDataSet> ReadDataAsync(Stream stream, string format)
{
    var factory = new CustomReaderFactory();
    var reader = factory.CreateReader(format);
    return await reader.ReadAsync(stream);
}
```

## Extension Points

### Format Support
- Implement new format readers
- Add custom data type handling
- Extend configuration options
- Add format-specific features

### Data Processing
- Custom data transformation
- Data validation
- Error handling
- Progress reporting

## Best Practices

1. **Error Handling**
   - Implement proper error handling
   - Provide meaningful error messages
   - Handle format-specific exceptions
   - Support error recovery

2. **Performance**
   - Use streaming where possible
   - Implement efficient memory usage
   - Support large file handling
   - Consider async operations

3. **Data Validation**
   - Validate input format
   - Check data consistency
   - Verify column types
   - Handle missing data

4. **Configuration**
   - Support flexible configuration
   - Allow format-specific options
   - Enable feature toggles
   - Provide sensible defaults

## Integration

### With DataStructures
```csharp
public class IntegratedReader : DataSetReaderBase
{
    protected override IDataSet ReadDataSetFromStream(Stream stream)
    {
        var dataSet = new DataSet();
        // Implement reading logic that creates proper DataStructures
        return dataSet;
    }
}
```

### With Message Hub
```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddSingleton<IDataSetReaderFactory, CustomReaderFactory>()
    )
);
```

## Testing Support

The library includes base test classes to help implement reader tests:

```csharp
public class CustomReaderTests : DataSetReaderTestBase
{
    protected override void ValidateDataSet(IDataSet dataSet)
    {
        // Implement format-specific validation
    }
    
    [Fact]
    public async Task ReadsValidFile()
    {
        var reader = new CustomFormatReader();
        var result = await reader.ReadAsync(GetTestStream());
        ValidateDataSet(result);
    }
}
```

## Related Projects

- MeshWeaver.DataSetReader.Excel - Excel format implementation
- MeshWeaver.DataSetReader.Csv - CSV format implementation
- MeshWeaver.DataStructures - Core data structures

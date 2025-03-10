# MeshWeaver.DataSetReader.Csv

MeshWeaver.DataSetReader.Csv is a specialized implementation of the DataSetReader framework for reading and writing CSV (Comma-Separated Values) files. It provides robust support for CSV format handling with advanced features for data type inference and custom formatting.

## Overview

The library provides:
- Full CSV format support with custom delimiters
- Header row detection and handling
- Type-safe data reading and writing
- Support for multi-table CSV files
- Special format handling for quoted values
- Dataset name and format metadata

## Features

### CSV Format Support
- Custom delimiter configuration
- Quote handling for complex values
- Multi-line value support
- Empty value handling
- Header row customization

### Data Type Handling
- Automatic type inference
- Custom type mapping
- Null value support
- List type support with configurable lengths
- Property-based mapping

### Special Markers
```
TablePrefix: @@     // Marks the start of a new table
FormatPrefix: $$    // Specifies the format of the data
DataSetNamePrefix: ## // Defines the dataset name
```

## Usage Examples

### Basic CSV Reading
```csharp
public async Task<IDataSet> ReadCsvFile(Stream stream)
{
    var options = new DataSetReaderOptions
    {
        Delimiter = ',',
        WithHeaderRow = true
    };
    
    var (dataSet, format) = await DataSetCsvSerializer.ReadAsync(stream, options);
    return dataSet;
}
```

### Writing Data to CSV
```csharp
public string WriteToCsv(IDataSet dataSet)
{
    // Serialize with comma delimiter
    return DataSetCsvSerializer.Serialize(dataSet, ',');
}
```

### Reading Typed Data
```csharp
public class Employee
{
    [MappingOrder(Order = 1)]
    public string Name { get; set; }
    
    [MappingOrder(Order = 2)]
    public decimal Salary { get; set; }
    
    [MappingOrder(Order = 3, Length = 5)]
    public IList<string> Skills { get; set; }
}

public async Task<IDataSet> ReadTypedCsv(Stream stream)
{
    var (dataSet, _) = await DataSetCsvSerializer.Parse(
        new StreamReader(stream),
        ',',
        true,
        typeof(Employee)
    );
    return dataSet;
}
```

### Multi-Table CSV
```csv
@@Employees
Name,Department,Salary
John Doe,IT,50000
Jane Smith,HR,45000

@@Departments
Code,Name,Budget
IT,Information Technology,1000000
HR,Human Resources,500000
```

## Configuration Options

### DataSetReaderOptions
```csharp
public class DataSetReaderOptions
{
    public char Delimiter { get; set; } = ',';
    public bool WithHeaderRow { get; set; } = true;
    public Type ContentType { get; set; }
}
```

## Advanced Features

### Quote Handling
- Supports escaped quotes in values
- Multi-line quoted values
- Automatic quote detection and parsing

### Type Mapping
```csharp
[MappingOrder(Order = 1, Length = 3)]
public IList<decimal> Prices { get; set; }
```
This will create three columns: Prices0, Prices1, Prices2

### Format Specification
```csv
$$CustomFormat
@@Table1
Column1,Column2
Value1,Value2
```

## Best Practices

1. **Header Handling**
   - Always specify whether headers are present
   - Use meaningful column names
   - Validate header consistency

2. **Data Types**
   - Use appropriate type mapping
   - Handle null values properly
   - Validate data type conversion

3. **Performance**
   - Use streaming for large files
   - Consider memory usage for multi-table files
   - Batch process when possible

4. **Error Handling**
   - Validate CSV format
   - Handle malformed data
   - Provide meaningful error messages

## Integration

### With DataStructures
```csharp
public async Task<IDataSet> ImportCsvToDataStructures(Stream stream)
{
    var options = new DataSetReaderOptions
    {
        Delimiter = ',',
        WithHeaderRow = true
    };
    
    var (dataSet, _) = await DataSetCsvSerializer.ReadAsync(stream, options);
    // DataSet is already in DataStructures format
    return dataSet;
}
```

### With Message Hub
```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddSingleton<IDataSetReader>(provider => 
            new CsvDataSetReader(new DataSetReaderOptions()))
    )
);
```

## Error Handling

The library provides detailed error information for common CSV issues:
- Malformed CSV structure
- Data type conversion errors
- Missing required columns
- Duplicate table names
- Invalid quote sequences

## Related Projects

- MeshWeaver.DataSetReader - Base reader framework
- MeshWeaver.DataStructures - Core data structures
- MeshWeaver.DataSetReader.Excel - Excel format implementation

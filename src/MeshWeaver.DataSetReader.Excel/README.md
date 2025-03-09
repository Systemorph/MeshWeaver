# MeshWeaver.DataSetReader.Excel

MeshWeaver.DataSetReader.Excel provides the foundational framework for reading Excel files in the MeshWeaver ecosystem. It serves as the base implementation for both binary (.xls) and OpenXML (.xlsx) Excel format readers.

## Overview

The library provides:
- Abstract base classes for Excel file reading
- Common Excel format handling functionality
- Factory pattern for reader creation
- Support for custom document properties
- Multi-sheet data handling

## Architecture

### Core Components

#### ExcelDataSetReaderBase
Base abstract class providing common Excel reading functionality:
- Sheet-to-table conversion
- Column name handling
- Data type conversion
- Row reading and processing

#### ExcelReaderFactory
Factory class for creating appropriate readers:
- Binary format (.xls) reader creation
- OpenXML format (.xlsx) reader creation
- Configuration options support
- Stream handling

### Format Support

The framework supports two Excel formats through specialized implementations:
- **Binary Format** (.xls)
  - Legacy Excel format
  - Implemented in `MeshWeaver.DataSetReader.Excel.BinaryFormat`
  
- **OpenXML Format** (.xlsx)
  - Modern Excel format
  - Implemented in `MeshWeaver.DataSetReader.Excel.OpenXmlFormat`

## Usage Examples

### Creating Excel Readers

```csharp
public class ExcelReader
{
    private readonly IExcelReaderFactory _factory;

    public ExcelReader()
    {
        _factory = new ExcelReaderFactory();
    }

    public IExcelDataReader CreateReader(Stream stream, bool isBinaryFormat)
    {
        return isBinaryFormat 
            ? _factory.CreateBinaryReader(stream)
            : _factory.CreateOpenXmlReader(stream);
    }
}
```

### Reading Excel Data

```csharp
public class ExcelDataReader : ExcelDataSetReaderBase
{
    private readonly IExcelReaderFactory _factory;
    private readonly bool _isBinaryFormat;

    protected override IExcelDataReader GetExcelDataReader(Stream stream)
    {
        return _isBinaryFormat
            ? _factory.CreateBinaryReader(stream)
            : _factory.CreateOpenXmlReader(stream);
    }
}
```

### Handling Custom Properties

```csharp
public async Task<string> GetExcelFormat(Stream stream)
{
    using var document = SpreadsheetDocument.Open(stream, false);
    return document.CustomFilePropertiesPart?
        .Properties?
        .Elements<CustomDocumentProperty>()
        .FirstOrDefault(x => x.Name == "Format")?
        .InnerText;
}
```

## Configuration Options

### ReadOption
```csharp
public class ReadOption
{
    public bool IsFirstRowAsColumnNames { get; set; } = true;
    public bool ConvertOADates { get; set; } = true;
    public int SheetIndex { get; set; } = 0;
}
```

## Advanced Features

### Multi-Sheet Handling
```csharp
protected (IDataSet DataSet, string Format) ReadAllSheets(Stream stream)
{
    var dataSet = new DataSet();
    using var reader = GetExcelReader(stream);
    
    while (reader.NextResult())
    {
        var table = new DataTable(reader.Name);
        // Read sheet data into table
        dataSet.Tables.Add(table);
    }
    
    return (dataSet, GetFormat(stream));
}
```

### Column Name Management
```csharp
private static string GetUniqueColumnName(
    string desiredName, 
    IDataColumnCollection columns)
{
    var num = 1;
    while (columns.Contains(desiredName))
    {
        desiredName = $"{desiredName}{num++}";
    }
    return desiredName;
}
```

## Best Practices

1. **Stream Handling**
   - Use proper stream disposal
   - Handle large files efficiently
   - Consider memory constraints

2. **Format Detection**
   - Validate file format before reading
   - Handle format-specific features appropriately
   - Support format conversion if needed

3. **Data Type Handling**
   - Handle Excel-specific data types
   - Convert dates properly
   - Manage null values

4. **Error Management**
   - Handle corrupted files
   - Provide format-specific error messages
   - Support recovery options

## Integration

### With DataStructures
```csharp
public async Task<IDataSet> ImportExcelToDataStructures(
    Stream stream, 
    bool isBinaryFormat)
{
    var reader = new ExcelDataReader(_factory, isBinaryFormat);
    var (dataSet, _) = reader.ReadDataSetFromFile(stream);
    return dataSet;
}
```

### With Message Hub
```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddSingleton<IExcelReaderFactory, ExcelReaderFactory>()
        .AddTransient<IDataSetReader, ExcelDataReader>()
    )
);
```

## Extension Points

### Custom Readers
Extend `ExcelDataSetReaderBase` for specialized reading:
```csharp
public class CustomExcelReader : ExcelDataSetReaderBase
{
    protected override IExcelDataReader GetExcelDataReader(Stream stream)
    {
        // Implement custom reader logic
    }
}
```

### Format-Specific Features
```csharp
public interface IExcelFormatHandler
{
    bool CanHandle(Stream stream);
    IExcelDataReader CreateReader(Stream stream);
    string GetFormat(Stream stream);
}
```

## Related Projects

- MeshWeaver.DataSetReader.Excel.BinaryFormat - Implementation for .xls files
- MeshWeaver.DataSetReader.Excel.OpenXmlFormat - Implementation for .xlsx files
- MeshWeaver.DataSetReader - Base reader framework
- MeshWeaver.DataStructures - Core data structures

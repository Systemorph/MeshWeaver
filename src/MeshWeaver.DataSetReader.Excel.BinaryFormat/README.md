# MeshWeaver.DataSetReader.Excel.BinaryFormat

MeshWeaver.DataSetReader.Excel.BinaryFormat is a specialized implementation for reading legacy Excel (.xls) files in the BIFF (Binary Interchange File Format) format. It provides comprehensive support for reading Excel 97-2003 workbooks with their complex binary structure.

## Overview

The library provides:
- Complete BIFF format support
- Excel 97-2003 workbook reading
- Cell format handling
- Shared string table support
- Multi-sheet workbook handling
- Formula cell support

## Architecture

### Core Components

#### ExcelBinaryReader
Main reader implementation providing:
- Workbook structure parsing
- Sheet content reading
- Cell value extraction
- Format conversion

#### BIFF Record Types
Specialized handlers for various BIFF records:
- BOF (Beginning of File)
- Worksheet data
- Cell content
- Formatting information
- Shared strings
- Formula cells

### Binary Format Support

#### Workbook Structure
- File header parsing
- Workbook globals
- Sheet information
- Format definitions
- Font tables

#### Cell Types
- Label cells
- Number cells
- Formula cells
- Blank cells
- Error cells
- Boolean cells

## Usage Examples

### Basic File Reading
```csharp
public async Task<IDataSet> ReadXlsFile(Stream stream)
{
    var reader = new ExcelBinaryReader();
    reader.Initialize(stream);
    
    var dataSet = new DataSet();
    while (reader.Read())
    {
        // Process rows
        for (int i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.GetValue(i);
            // Handle cell value
        }
    }
    
    return dataSet;
}
```

### Configuring Reader Options
```csharp
var reader = new ExcelBinaryReader(new ReadOption
{
    IsFirstRowAsColumnNames = true,
    ConvertOADates = true
});
```

### Reading Multiple Sheets
```csharp
public DataSet ReadAllSheets(Stream stream)
{
    using var reader = new ExcelBinaryReader();
    reader.Initialize(stream);
    
    var dataSet = new DataSet();
    do
    {
        var table = new DataTable(reader.Name);
        // Read sheet data into table
        dataSet.Tables.Add(table);
    } while (reader.NextResult());
    
    return dataSet;
}
```

## Advanced Features

### Shared String Handling
```csharp
private void HandleSharedStrings(XlsBiffSST sst)
{
    // SST (Shared String Table) processing
    foreach (var str in sst.StringList)
    {
        // Process shared string
    }
}
```

### Format Conversion
```csharp
private object ConvertCellValue(XlsBiffRecord cell, ushort format)
{
    // Handle various Excel formats
    switch (format)
    {
        case FormatType.Date:
            return TryConvertOADateTime(cell.Value);
        case FormatType.Number:
            return ConvertNumber(cell.Value);
        // ... other formats
    }
}
```

## BIFF Record Types

### Core Records
- BOF (Beginning of File)
- EOF (End of File)
- BOUNDSHEET (Worksheet Information)
- SST (Shared String Table)
- FORMAT (Number Format)
- XF (Extended Format)

### Cell Records
- LABEL (String Cell)
- NUMBER (Numeric Cell)
- FORMULA (Formula Cell)
- BLANK (Empty Cell)
- MULBLANK (Multiple Empty Cells)
- RK (RK Number Cell)

## Best Practices

1. **Memory Management**
   - Use streaming for large files
   - Dispose readers properly
   - Handle large string tables efficiently

2. **Format Handling**
   - Validate cell formats
   - Handle date conversions properly
   - Support custom number formats

3. **Error Handling**
   - Check file corruption
   - Handle formula errors
   - Validate record sequences

4. **Performance**
   - Use appropriate buffer sizes
   - Cache shared strings
   - Optimize cell access

## Integration

### With Base Excel Reader
```csharp
public class BinaryFormatReader : ExcelDataSetReaderBase
{
    protected override IExcelDataReader GetExcelDataReader(Stream stream)
    {
        var reader = new ExcelBinaryReader();
        reader.Initialize(stream);
        return reader;
    }
}
```

### With Message Hub
```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddSingleton<IExcelReaderFactory>(provider => 
            new ExcelReaderFactory())
        .AddTransient<IDataSetReader, BinaryFormatReader>()
    )
);
```

## Error Handling

The library provides detailed error information for:
- Invalid file formats
- Corrupted records
- Unsupported features
- Formula errors
- Format conversion issues

## Related Projects

- MeshWeaver.DataSetReader.Excel - Base Excel reader framework
- MeshWeaver.DataSetReader.Excel.OpenXmlFormat - Modern Excel format implementation
- MeshWeaver.DataStructures - Core data structures

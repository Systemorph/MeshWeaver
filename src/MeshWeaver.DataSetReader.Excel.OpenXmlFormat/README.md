# MeshWeaver.DataSetReader.Excel.OpenXmlFormat

MeshWeaver.DataSetReader.Excel.OpenXmlFormat is a specialized implementation for reading modern Excel (.xlsx) files using the Office Open XML format. It provides comprehensive support for reading Excel 2007+ workbooks with their XML-based structure.

## Overview

The library provides:
- Complete Office Open XML format support
- Excel 2007+ workbook reading
- XML-based structure parsing
- Shared string handling
- Style and formatting support
- Cell type conversion

## Architecture

### Core Components

#### ExcelOpenXmlReader
Main reader implementation providing:
- ZIP archive handling
- XML content parsing
- Sheet data extraction
- Cell value conversion

#### Workbook Components
- XlsxWorkbook - Workbook structure and metadata
- XlsxWorksheet - Individual worksheet handling
- XlsxStyles - Style definitions and formatting
- XlsxDimension - Sheet dimensions and references

### OpenXML Structure Support

#### Package Components
- Workbook XML
- Worksheet XMLs
- Shared Strings XML
- Styles XML
- Relationships XML

#### XML Namespaces
```csharp
public static class Namespaces
{
    public const string SpreadsheetMl = 
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string Relationships = 
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
}
```

## Usage Examples

### Basic File Reading
```csharp
public async Task<IDataSet> ReadXlsxFile(Stream stream)
{
    var reader = new ExcelOpenXmlReader();
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

### Reading with Shared Strings
```csharp
private object GetCellValue(XElement cell, XlsxWorkbook workbook)
{
    var cellType = cell.Attribute("t")?.Value;
    var value = cell.Element(ns + "v")?.Value;
    
    if (cellType == "s") // Shared string
    {
        return workbook.SharedStrings[int.Parse(value)];
    }
    
    return value;
}
```

### Style Handling
```csharp
private object FormatCellValue(object value, XlsxXf style)
{
    if (style.ApplyNumberFormat && IsDateTimeStyle(style.NumFmtId))
    {
        return ConvertFromOATime((double)value);
    }
    
    return value;
}
```

## Advanced Features

### ZIP Archive Handling
```csharp
public class ZipWorker
{
    public Stream GetWorkbookStream()
    {
        return GetEntryStream("xl/workbook.xml");
    }
    
    public Stream GetSharedStringsStream()
    {
        return GetEntryStream("xl/sharedStrings.xml");
    }
}
```

### Dimension Management
```csharp
public class XlsxDimension
{
    public int FirstRow { get; }
    public int LastRow { get; }
    public int FirstCol { get; }
    public int LastCol { get; }
    
    public XlsxDimension(string reference)
    {
        // Parse Excel dimension reference (e.g., "A1:Z100")
    }
}
```

## XML Structure

### Workbook XML
```xml
<workbook xmlns="...">
  <sheets>
    <sheet name="Sheet1" sheetId="1" r:id="rId1"/>
    <sheet name="Sheet2" sheetId="2" r:id="rId2"/>
  </sheets>
</workbook>
```

### Worksheet XML
```xml
<worksheet xmlns="...">
  <dimension ref="A1:Z100"/>
  <sheetData>
    <row r="1">
      <c r="A1" t="s">
        <v>0</v>
      </c>
    </row>
  </sheetData>
</worksheet>
```

## Best Practices

1. **Memory Management**
   - Use streaming XML parsing
   - Handle large shared string tables
   - Manage ZIP archive resources
   - Clean up temporary streams

2. **Performance**
   - Cache shared strings
   - Optimize XML parsing
   - Use efficient cell access
   - Minimize memory allocations

3. **Error Handling**
   - Validate XML structure
   - Handle corrupt ZIP archives
   - Manage missing components
   - Validate cell references

4. **Format Support**
   - Handle all cell types
   - Support date/time formats
   - Process custom number formats
   - Manage style inheritance

## Integration

### With Base Excel Reader
```csharp
public class OpenXmlFormatReader : ExcelDataSetReaderBase
{
    protected override IExcelDataReader GetExcelDataReader(Stream stream)
    {
        var reader = new ExcelOpenXmlReader();
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
        .AddTransient<IDataSetReader, OpenXmlFormatReader>()
    )
);
```

## Error Handling

The library provides detailed error information for:
- Invalid XML structure
- Corrupt ZIP archives
- Missing package parts
- Invalid cell references
- Format conversion errors

## Related Projects

- MeshWeaver.DataSetReader.Excel - Base Excel reader framework
- MeshWeaver.DataSetReader.Excel.BinaryFormat - Legacy Excel format implementation
- MeshWeaver.DataStructures - Core data structures

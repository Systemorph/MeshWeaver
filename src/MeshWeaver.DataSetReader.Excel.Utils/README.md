# MeshWeaver.DataSetReader.Excel.Utils

MeshWeaver.DataSetReader.Excel.Utils is a utility library that provides common functionality and abstractions for Excel file reading in the MeshWeaver ecosystem. It serves as the foundation for both binary (.xls) and OpenXML (.xlsx) format readers.

## Overview

The library provides:
- Core interfaces for Excel data reading
- Common utility functions for Excel data processing
- Error handling and exception types
- ZIP archive handling for OpenXML formats
- Format conversion utilities
- Data type management

## Core Components

### IExcelDataReader Interface
The main interface for Excel data reading implementations:
```csharp
public interface IExcelDataReader : IDataReader
{
    void Initialize(Stream fileStream);
    DataSet AsDataSet();
    DataSet AsDataSet(bool convertOADateTime);
    bool IsValid { get; }
    string Name { get; }
    int ResultsCount { get; }
    bool IsFirstRowAsColumnNames { get; set; }
}
```

### Utility Functions

#### Data Conversion
```csharp
public static class Helpers
{
    // Convert Excel OLE Automation date to DateTime
    public static object ConvertFromOATime(double value);
    
    // Convert escaped characters in strings
    public static string ConvertEscapeChars(string input);
    
    // Convert Int64 bits to double (for binary formats)
    public static double Int64BitsToDouble(long value);
}
```

#### Data Type Management
```csharp
public static class Helpers
{
    // Fix and optimize data types in DataSet
    public static void FixDataTypes(DataSet dataset);
    
    // Handle duplicate column names
    public static void AddColumnHandleDuplicate(DataTable table, string columnName);
}
```

### ZIP Archive Handling
```csharp
public class ZipWorker
{
    // Access workbook and shared strings streams
    public Stream GetWorkbookStream();
    public Stream GetSharedStringsStream();
    public Stream GetStylesStream();
}
```

### Constants and Formats
```csharp
public static class ExcelConstants
{
    // Common Excel format constants
    public const int MaxWorksheetColumns = 16384;
    public const int MaxWorksheetRows = 1048576;
    public const int MaxWorksheetNameLength = 31;
}
```

## Error Handling

### Exception Types
- `BiffRecordException` - For BIFF record parsing errors
- `HeaderException` - For header parsing errors
- `ExcelReaderException` - Base exception for Excel reading errors

### Error Messages
```csharp
public static class Errors
{
    public const string InvalidPassword = "Invalid password";
    public const string InvalidFile = "Invalid file";
    public const string NotSupported = "Not supported";
}
```

## Features

1. **Format Support**
   - Binary (.xls) format utilities
   - OpenXML (.xlsx) format utilities
   - Common abstractions for both formats

2. **Data Processing**
   - Stream-based reading
   - Data type inference
   - Column name handling
   - Date/time conversion
   - Character encoding support

3. **Error Handling**
   - Specialized exception types
   - Validation checks
   - Error messages
   - Recovery mechanisms

4. **Performance Optimizations**
   - Efficient data type handling
   - Stream management
   - Memory optimization
   - Resource cleanup

## Best Practices

1. **Resource Management**
   ```csharp
   using (var stream = File.OpenRead("data.xlsx"))
   {
       var reader = new ExcelReader();
       reader.Initialize(stream);
       // Process data
   }
   ```

2. **Data Type Handling**
   ```csharp
   var dataset = reader.AsDataSet();
   Helpers.FixDataTypes(dataset); // Optimize data types
   ```

3. **Error Handling**
   ```csharp
   try
   {
       reader.Initialize(stream);
       if (!reader.IsValid)
           throw new ExcelReaderException(reader.ExceptionMessage);
   }
   catch (BiffRecordException ex)
   {
       // Handle BIFF format errors
   }
   ```

## Integration

### With Excel Readers
```csharp
public class ExcelReader : IExcelDataReader
{
    protected readonly ZipWorker ZipWorker;
    
    public void Initialize(Stream stream)
    {
        ZipWorker.Initialize(stream);
        // Initialize reader
    }
}
```

### With Message Hub
```csharp
services.AddMessageHub(hub => hub
    .ConfigureServices(services => services
        .AddTransient<IExcelDataReader, ExcelReader>()
    )
);
```

## Related Projects

- MeshWeaver.DataSetReader.Excel - Base Excel reader framework
- MeshWeaver.DataSetReader.Excel.BinaryFormat - Legacy Excel format implementation
- MeshWeaver.DataSetReader.Excel.OpenXmlFormat - Modern Excel format implementation

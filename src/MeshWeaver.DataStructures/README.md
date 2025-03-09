# MeshWeaver.DataStructures

MeshWeaver.DataStructures is a lightweight and efficient library for managing tabular data sets. It provides a streamlined alternative to ADO.NET DataSet with reduced overhead, improved performance, and lower memory consumption.

## Overview

The library provides a robust framework for:
- Importing and managing tabular data
- Efficient in-memory data representation
- Serialization and deserialization of data sets
- Type-safe data access and manipulation
- Integration with ADO.NET DataSets

## Key Features

### Efficient Data Management
- Lightweight data structures optimized for performance
- Low memory footprint compared to ADO.NET DataSet
- Fast data access and manipulation operations
- Support for large datasets

### Flexible Data Model
- Tables with typed columns
- Row-based data access
- Column format specifications
- Support for null values
- Dynamic table and column creation

### Data Import/Export
- XML serialization support
- JSON serialization support
- ADO.NET DataSet conversion
- Streaming data import/export

## Usage Examples

### Creating a DataSet
```csharp
// Create a new dataset
var dataSet = new DataSet("MyDataSet");

// Add a table
var table = dataSet.Tables.Add("Employees");

// Define columns
table.Columns.Add("Id", typeof(int));
table.Columns.Add("Name", typeof(string));
table.Columns.Add("Salary", typeof(decimal));

// Add rows
var row = table.NewRow();
row["Id"] = 1;
row["Name"] = "John Doe";
row["Salary"] = 50000m;
table.Rows.Add(row);
```

### Working with Data
```csharp
// Access data by column name
foreach (var row in table.Rows)
{
    var name = row["Name"].ToString();
    var salary = row.Field<decimal>("Salary");
    // Process data...
}

// Access data by column index
foreach (var row in table.Rows)
{
    var id = row[0];
    var name = row[1];
    // Process data...
}
```

### Serialization
```csharp
// JSON serialization
var jsonSerializer = DataSetJsonSerializer.Instance;
var json = jsonSerializer.Serialize(dataSet, indent: true);

// XML serialization
var xmlSerializer = DataSetXmlSerializer.Instance;
var xml = xmlSerializer.Serialize(dataSet);
```

### ADO.NET Integration
```csharp
// Convert from ADO.NET DataSet
var factory = new DataSetFactory();
var adoDataSet = GetAdoNetDataSet(); // Your ADO.NET DataSet
var dataSet = factory.ConvertFromAdoNet(adoDataSet);

// Convert to ADO.NET DataSet
var adoDataSet = factory.ConvertToAdoNet(dataSet);
```

## Core Components

### IDataSet
- Main container for tables
- Manages collection of tables
- Supports merging datasets
- XML/JSON serialization support

### IDataTable
- Represents a single table
- Manages columns and rows
- Provides row creation and management
- Supports enumeration of rows

### IDataColumn
- Defines column metadata
- Specifies data type
- Supports column formatting
- Manages column indexing

### IDataRow
- Represents a single row of data
- Provides typed access to values
- Supports both index and name-based access
- Includes value conversion helpers

## Best Practices

1. Use strongly-typed column definitions when possible
2. Leverage the Field<T> method for type-safe data access
3. Consider memory usage for large datasets
4. Use appropriate serialization format based on needs
5. Implement proper error handling for data conversions

## Performance Considerations

- Use indexed access when possible
- Batch row additions for better performance
- Consider using bulk operations for large datasets
- Cache column indexes for repeated access
- Use appropriate data types for columns

## Integration

The library integrates seamlessly with:
- ADO.NET DataSets
- XML processing pipelines
- JSON-based systems
- Custom data import/export solutions

## Features
- Data models and entities for DataStructures
- Repository implementations
- Data access patterns
- Query capabilities
- Data transformation utilities
- Serialization support
- Integration with core data systems

## Usage
```csharp
// Configure data services
services.AddMeshWeaverDataStructuresServices(options => {
    options.ConnectionString = Configuration.GetConnectionString("DefaultConnection");
});

// Use data repositories
public class DataStructuresService
{
    private readonly IDataStructuresRepository _repository;
    
    public DataStructuresService(IDataStructuresRepository repository)
    {
        _repository = repository;
    }
    
    public async Task<DataStructures> GetByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id);
    }
}
```

## Data Models
- Domain-specific entities
- Data transfer objects
- Validation rules
- Data relationships

## Integration
- Works with MeshWeaver.Data core
- Plugs into MeshWeaver data pipeline
- Supports MeshWeaver visualization components

## Related Projects
- [MeshWeaver.Data](../MeshWeaver.Data/README.md) - Core data functionality
- [MeshWeaver.Data.Contract](../MeshWeaver.Data.Contract/README.md) - Data contracts

## See Also
Refer to the [main MeshWeaver documentation](../../Readme.md) for more information about the overall project.

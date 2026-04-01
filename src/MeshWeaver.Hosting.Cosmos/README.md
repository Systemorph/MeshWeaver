# MeshWeaver.Hosting.Cosmos

Azure Cosmos DB persistence provider for MeshWeaver. Provides storage adapters for persisting mesh nodes and related data to Cosmos DB.

## Usage

```csharp
builder.AddCosmosPersistence(connectionString, databaseName);
```

## Features

- Cosmos DB storage adapter for mesh node persistence
- Partition-aware storage with configurable container mappings
- Integration with MeshWeaver's persistence pipeline

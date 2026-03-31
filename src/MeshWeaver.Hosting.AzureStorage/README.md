# MeshWeaver.Hosting.AzureStorage

Provides Azure Blob Storage as a persistence backend for MeshWeaver's graph data, implementing `IStorageAdapter` for reading, writing, and listing MeshNodes in blob containers.

## Features

- `AzureBlobStorageAdapter` implementing `IStorageAdapter` with full CRUD for MeshNodes
- Multi-format support: `.md` (with YAML front matter) and `.json`, with priority-based format detection
- Automatic format migration (cleans up old extensions when format changes)
- Partition object storage for satellite data (threads, activities, annotations)
- `AzureBlobStorageAdapterFactory` for config-driven storage setup via `AddPersistenceFromConfig`
- Hierarchical path listing with child node and directory path enumeration
- Container auto-creation support

## Usage

```csharp
// Option 1: Register the factory for config-driven setup
services.AddAzureBlobStorageFactory();

// Option 2: Direct registration with connection string
services.AddAzureBlobPersistence(
    connectionString: "DefaultEndpointsProtocol=https;...",
    containerName: "graph-data");

// Option 3: Direct registration with BlobContainerClient
services.AddAzureBlobPersistence(containerClient);
```

## Dependencies

- `MeshWeaver.Hosting` -- `IStorageAdapter` interface and persistence infrastructure
- `Azure.Storage.Blobs` -- Azure Blob Storage SDK
- `Azure.Data.Tables` -- Azure Table Storage SDK

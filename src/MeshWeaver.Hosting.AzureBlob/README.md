# MeshWeaver.Hosting.AzureBlob

## Overview
MeshWeaver.Hosting.AzureBlob provides Azure Blob Storage integration for MeshWeaver articles. It enables storing and managing article content in Azure Blob containers, making it ideal for scalable content management.

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);

// Add Azure Blob Storage client
builder.AddKeyedAzureBlobClient(StorageProviders.Articles);

// Configure services with Azure Blob storage for articles
builder.UseMeshWeaver(
    new MeshAddress(),
    config => config
        .ConfigureWebPortal()
        .ConfigureServices(services => 
            services.AddAzureBlobArticles()
        )
);

var app = builder.Build();
app.StartPortalApplication();
```

## Features
- Azure Blob Storage integration
- Article content management
- Automatic container creation
- Blob lifecycle management
- Content versioning support
- Concurrent access handling

## Configuration
The Azure Blob storage provider can be configured through standard Azure configuration patterns:

```json
{
  "Azure": {
    "Storage": {
      "Articles": {
        "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;",
        "ContainerName": "articles"
      }
    }
  }
}
```

## Integration
- Works with [MeshWeaver.Hosting](../MeshWeaver.Hosting/README.md)
- Compatible with both monolithic and Orleans hosting
- Integrates with Azure SDK for .NET

## See Also
- [Azure Blob Storage Documentation](https://learn.microsoft.com/azure/storage/blobs/) - Learn more about Azure Blob Storage
- [Main MeshWeaver Documentation](../../Readme.md) - More about MeshWeaver hosting options

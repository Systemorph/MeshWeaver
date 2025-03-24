# MeshWeaver.Hosting.PostgreSql

## Overview
MeshWeaver.Hosting.PostgreSql provides PostgreSQL-based hosting capabilities for MeshWeaver applications. This library enables persistent storage and state management using PostgreSQL as the backend database, offering reliable data persistence for MeshWeaver instances.

## Features
- PostgreSQL-based persistence for MeshWeaver data
- Efficient schema management
- Support for both direct connection strings and named connections
- Resilient connections with automatic retry on failure
- Transaction management
- High-performance data access patterns
- Integration with MeshWeaver hosting infrastructure

## Usage

We recommend using PostgreSQL together with Aspire. The corresponding extension method is meant to point to a connectionName, which will be configured in aspire. 

### Integration with Aspire

#### AppHost Configuration
```csharp
// In Program.cs of your Aspire AppHost project
var builder = DistributedApplication.CreateBuilder(args);

// Set up PostgreSQL
var postgres = builder
    .AddPostgres("postgres")
    .WithPgAdmin()
    .WithDataVolume();

var meshweaverdb = postgres.AddDatabase("meshweaverdb");

// Reference the database in your services
var frontend = builder
    .AddProject<Projects.MeshWeaver_Portal_Web>("frontend")
    .WithReference(meshweaverdb);
```

#### Client Project Configuration
```csharp
// In Program.cs of your service project
var builder = WebApplication.CreateBuilder(args);

// Add service defaults and connection
builder.AddServiceDefaults();

// Configure PostgreSQL
builder.ConfigurePostgreSqlContext("meshweaverdb");

var app = builder.Build();
app.Run();
```

## Integration
- Works with MeshWeaver.Hosting core infrastructure
- Supports data persistence for MeshWeaver applications
- Complements other hosting options for different deployment scenarios
- Integrates with MeshWeaver's data ecosystem
- Compatible with Microsoft Aspire for cloud-native applications

## Related Projects
- MeshWeaver.Hosting - Core hosting functionality
- MeshWeaver.Hosting.Monolith - Single-process hosting
- MeshWeaver.Hosting.Orleans - Distributed hosting
- MeshWeaver.Data - Core data functionality

## See Also
Refer to the main MeshWeaver documentation for more information about hosting options and configuration.
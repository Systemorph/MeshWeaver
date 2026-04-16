# Memex.Portal.Monolith

## Overview
Memex.Portal.Monolith is the recommended single-process development portal for Memex. It hosts the full MeshWeaver mesh in-process with file-system-based graph storage and sample data sources, making it the fastest way to run the application locally.

## Usage
```bash
dotnet run --project memex/Memex.Portal.Monolith
# Access at https://localhost:7122
```

## Features
- Single-process deployment with in-memory messaging via `UseMonolithMesh()`
- File-system graph storage with formatted JSON in development mode
- Sample data sources (ACME, Northwind, Cornerstone, FutuRe) loaded from `samples/Graph/Data/`
- Local data protection key persistence
- Aspire service defaults (health checks, OpenTelemetry)
- Static content collection serving at the mesh level

## Integration
- Built on [MeshWeaver.Hosting.Monolith](../../src/MeshWeaver.Hosting.Monolith/README.md) for single-process hosting
- Uses [Memex.Portal.Shared](../Memex.Portal.Shared/) for portal configuration, authentication, and UI
- Uses [Memex.Portal.ServiceDefaults](../aspire/Memex.Portal.ServiceDefaults/) for observability

## See Also
For distributed deployment with Orleans, see [Memex.Portal.Distributed](../aspire/Memex.Portal.Distributed/).

# Memex.Portal.ServiceDefaults

## Overview
Memex.Portal.ServiceDefaults is a shared Aspire project that provides common cross-cutting concerns for all Memex services: health checks, OpenTelemetry, service discovery, and HTTP resilience.

## Features
- **Health checks** — `/health` (readiness) and `/alive` (liveness) endpoints with 20-second timeouts
- **OpenTelemetry** — metrics (ASP.NET Core, HTTP, Orleans, runtime) and tracing with health endpoint filtering
- **Exporters** — automatic Azure Monitor or OTLP exporter selection based on configuration
- **Service discovery** — Aspire service discovery on all `HttpClient` instances
- **HTTP resilience** — standard resilience handler on all outbound HTTP calls
- **Cluster constants** — `MemexDistributedConstants` with shared `ClusterId` and `ServiceId`

## Usage
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
// ...
var app = builder.Build();
app.MapDefaultEndpoints();
```

## Integration
- Referenced by [Memex.Portal.Monolith](../../Memex.Portal.Monolith/), [Memex.Portal.Distributed](../Memex.Portal.Distributed/), and [Memex.Database.Migration](../Memex.Database.Migration/)
- Depends on [MeshWeaver.Hosting](../../../src/MeshWeaver.Hosting/) and [MeshWeaver.Mesh.Contract](../../../src/MeshWeaver.Mesh.Contract/)

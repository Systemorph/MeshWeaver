# Memex.Portal.ServiceDefaults

## Overview
Memex.Portal.ServiceDefaults is a shared Aspire project that provides common cross-cutting concerns for all Memex services: health checks, OpenTelemetry, service discovery, and HTTP resilience.

## Features
- **Health checks** — three endpoints, three questions, three predicates (see `ProbeEndpoints.cs`):
  `/health` (every check — the startup probe), `/alive` (checks tagged `live` — liveness, "am I making
  progress") and `/ready` (checks tagged `ready` — readiness, "can I take a request"). 20-second
  timeouts. 🚨 Readiness and liveness must never share a path — MeshWeaver#3330.
- **OpenTelemetry** — metrics (ASP.NET Core, HTTP, Orleans, runtime) and tracing with health endpoint filtering
- **Exporters** — OTLP exporter (to the Prometheus / Grafana / Loki stack) enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured
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

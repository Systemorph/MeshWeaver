# Aspire.Hosting.Memex

Aspire hosting integration for the MeshWeaver Memex portal. `builder.AddMemex(...)` composes
a complete portal deployment from published container images — no portal source tree needed —
and the standard Aspire publishers turn that one model into Docker Compose, Kubernetes/Helm,
or Azure Container Apps artifacts.

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddMemex("memex", o => o
    .WithBackend("Filesystem")
    .WithOrleansClustering("AdoNet")
    .WithImage(tag: "3.0.0-rc1"));

builder.Build().Run();
```

## What it wires

- PostgreSQL (pgvector) with a persistent volume
- The one-shot database migration container, ordered before the portal
- The portal container (`memex-portal` / `memex-portal-ai`) from the published image tag

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)

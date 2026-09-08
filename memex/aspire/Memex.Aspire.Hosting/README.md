# MeshWeaver.Aspire.Hosting.Memex

Aspire hosting integration for the MeshWeaver Memex portal. `builder.AddMemex(...)` composes a
complete portal deployment from published container images — no portal source tree needed — and
the instance is declared ONCE, as a **Deployment record**: the same `DeploymentContent` the Helm
chart renders from and the portal itself binds at boot. Every fluent call is a pure transform of
that record; the containers, volumes and environment are derived from it. The package bundles the
record assembly (`MeshWeaver.Deployment.Contract`), so one reference is all an AppHost needs.

## Usage

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddMemex("memex")
    .WithImage("ghcr.io/systemorph/memex-portal-ai", tag: "3.1.0")   // a release, or "3.1.0-ci.<n>"
    .WithOrleansClustering("AdoNet")
    .WithPluginRepo("plugins", "https://github.com/Systemorph/MeshWeaver.Plugins", gitRef: "main")
    .PreInstall("MeshWeaver.Plugins/Hosting")
    .WithRequiredModule("MeshWeaver.Hosting.Postgres")
    .WithSignIn(microsoftClientId: "<client id>", microsoftTenantId: "<tenant>")
    .WithSecret("Authentication__Microsoft__ClientSecret", builder.AddParameter("microsoft-client-secret", secret: true))
    .PublishRecord("deployments/memex.json");   // the record, ready for the setup wizard or a Provision action

builder.Build().Run();
```

`AddMemex(name, record)` and `AddMemexFromFile(name, path)` start from an existing record instead
of the default one. Secrets are never on the record — `WithSecret` hands a value to the container
directly (an Aspire parameter, or a literal in development); on Kubernetes the same keys come from
Key Vault through the record's `keyVaultSecrets` map.

## What it wires

- PostgreSQL (pgvector) with a persistent volume, plus the separate `orleans` clustering database
- The one-shot database migration container, ordered before the portal, at the SAME image tag
- The portal container from the record's image, with the record's volumes as named Docker volumes
- The record itself as configuration: `Deployment__Record` (the whole record as JSON) beside the
  derived per-key surface the Helm chart also renders — `Storage__*`, `Graph__Storage__*`,
  `PluginCatalog__*`, `Modules__Required__N`, `Authentication__*`, `Email__*`, …

The parity table (fluent method → record field → Helm value → configuration key) is
[Configuring an instance from Aspire](https://memex.meshweaver.cloud/Doc/Architecture/ConfiguringAnInstanceFromAspire).
Aspire emits a record, never a chart: the Kubernetes/Helm side renders from the same record on the
control instance.

## Links

- [MeshWeaver repository](https://github.com/Systemorph/MeshWeaver)
- [Documentation](https://memex.meshweaver.cloud/Doc)

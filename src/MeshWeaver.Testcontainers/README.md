# MeshWeaver.Testcontainers — a disposable memex as a test substrate

A [Testcontainers](https://dotnet.testcontainers.org/) module that starts the **portal image the
platform build produced** the way `Testcontainers.PostgreSql` starts a real Postgres: the test is a
client of a real memex, and the platform under test is exactly the image the pipeline promoted — no
core source checkout, no in-process mesh, no `MeshWeaver.Fixture`.

```csharp
await using INetwork network = new NetworkBuilder().Build();
await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
    .WithNetwork(network).WithNetworkAliases("postgres")
    .WithDatabase("memex").WithUsername("postgres").WithPassword("postgres")
    .Build();
await postgres.StartAsync(ct);
// …then run the memex-migration image of the SAME build once on `network` with
// ConnectionStrings__memex set, and require exit code 0 — the portal refuses an unmigrated schema.

await using var memex = new MemexBuilder()
    .WithImage("meshweaver.azurecr.io/memex-portal-ai@sha256:…")   // the pinned build under test
    .WithNetwork(network)
    .WithPostgres("Host=postgres;Port=5432;Database=memex;Username=postgres;Password=postgres")
    .Build();
await memex.StartAsync(ct);

// memex.BaseAddress, memex.McpEndpoint, memex.HealthEndpoint
```

## What the builder sets, and why

The portal image runs `Memex.Portal.Distributed` (Orleans + Postgres). A throwaway instance is:

| setting | value | reason |
|---|---|---|
| `Deployment:Backend` | `Filesystem` | Azure-free self-host backend; `Deployment:DataRoot=/tmp/memex-data` (the image runs non-root and cannot create `/data`) |
| `Features:Orleans:Clustering` | `Localhost` | single silo in one container |
| `ConnectionStrings:memex` | **required** — `WithPostgres(...)` | the instance's data; reachable *from inside* the container (a network alias, not a host-mapped port). Migrate it first with the `memex-migration` image of the same build — `DbVersionGate` refuses an unmigrated schema |
| `Graph:Storage:Type` | `PostgreSql` — set by `WithPostgres(...)` | the image bakes no storage type, so without it the container serves the first-run SETUP wizard and composes no mesh (#6037) |
| `Authentication:EnableDevLogin` | `true` (explicit) | the host forces it OFF unless the value is literally `true` |
| wait strategy | `MemexReadiness`: `/health` answers the startup census (`Healthy`/`Degraded`) and `/api/version` is not redirected to `/setup` | the MESH started, not merely the process — a probe path's flat `200 ok` is answered by the setup wizard too. A wizard, or a container that exits first, fails `StartAsync` at once with the reason |
| output | stdout/stderr → the test output | the container's log is the test's log |

`WithImage` is the consumer's: this module never guesses a tag.

## Where it fits

- **Black-box and substrate suites** (the Postgres hosting suites, end-to-end flows) use this — a
  memex on a Postgres, exercised over HTTP/MCP.
- **Engine suites that inject fakes through DI** keep an in-process mesh; that is a different
  primitive, not this one.
- The library is test support: `IsPackable=false`, never a NuGet package.

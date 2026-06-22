---
Description: "How a native MeshWeaver client bootstraps its own in-process monolith mesh on SQLite + file-system content, registers a device identity, reads its instance list from the mesh, and joins other meshes over SignalR — and the platform limits that shape it."
title: Local-First Client & Bootstrap
order: 35
---

# Local-First Client & Bootstrap

A native Memex client (the MAUI app) is **not** a thin shell around a remote portal. It hosts its **own in-process monolith mesh** — SQLite node storage + file-system content, fully offline — and **joins other meshes** (the public portal, a team portal, …) as a SignalR participant. Local data is a first-class mesh; remote portals are other meshes it federates with.

This is the inverse of the server: instead of one big partitioned portal, the client is a small mesh that *also* participates in bigger ones.

## The bootstrap sequence

The client builds the mesh, then uses the mesh to read its own config, then connects out. **Order matters** — the instance list lives *in* the mesh, so the mesh must exist before it can be read:

1. **Bootstrap the monolith** — `UseMonolithMesh` + SQLite persistence + file-system content, built into its **own MeshWeaver service provider**.
2. **Register the device identity** — one `AccessContext` for every local operation (single-user mesh).
3. **`GetQuery` the instance nodes** — read the `MemexInstance` nodes from the local mesh.
4. **Connect the SignalR meshes** — for each authenticated instance, dial its `/signalr` endpoint and register the stream.

## 1–2. The raw `MeshBuilder` bootstrap

The client uses the raw builder (no test base). Two things are easy to get wrong and both throw at runtime:

```csharp
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.AddLogging();
services.AddOptions();

var builder = new MeshBuilder(c => c.Invoke(services), AddressExtensions.CreateMeshAddress("local"))
    .UseMonolithMesh()
    .AddPartitionedSqlitePersistence($"Data Source={Path.Combine(appData, "memex-local.db")}")
    .AddRowLevelSecurity()
    .AddGraph()                 // standard node types (see platform note below)
    .AddSpaceType()
    .ConfigureServices(s => s.AddFileSystemAssemblyStore(Path.Combine(appData, "assembly-store")));
services.AddSingleton(builder.BuildHub);

// 🚨 CreateMeshWeaverServiceProvider — NOT BuildServiceProvider. The default skips the module
// setup and the hub throws "Mesh Weaver has not been properly configured".
var sp = services.CreateMeshWeaverServiceProvider();
var hub = sp.GetRequiredService<IMessageHub>();

// One device-user identity for every operation (PostPipeline fails closed without a context).
hub.ServiceProvider.GetRequiredService<AccessService>()
    .SetCircuitContext(new AccessContext { ObjectId = "device-user", Name = "Device User" });
```

- **`CreateMeshWeaverServiceProvider()`**, never `BuildServiceProvider()` — it runs the MeshWeaver module setup.
- The mesh has its **own** service provider, separate from the host's DI (the MAUI app resolves the hub from it).
- The address `CreateMeshAddress("local")` gives the local mesh the id **`local`**.

**Persistence**: nodes in SQLite (`MeshWeaver.Hosting.Sqlite` — the on-device counterpart to Postgres, which can't run on a phone), content via `AddFileSystemContentCollection`. See [Data Access Patterns](/Doc/Architecture/DataAccessPatterns).

## 3. Config as mesh nodes — read with `GetQuery`

The client's config is **not** a `Preferences`/JSON side store — it is mesh nodes. Each connectable mesh is a `MemexInstance` node (base URL + token) in the local mesh. The bootstrap reads them with `hub.GetQuery(...)` (per-user RLS query) and binds the UI to them via `GetMeshNodeStream` / `stream.Update` — the standard [Data Binding](/Doc/GUI/DataBinding) pattern, native variant in [Data Binding in a MAUI Client](/Doc/GUI/DataBindingMaui). The only on-device non-mesh state is the bootstrap secret (first token in `SecureStorage`).

## 4. Connecting to other meshes — a Settings feature of *every* mesh

Joining another mesh is **not** client-specific — it is a capability any mesh exposes in **Settings**: "connect to other meshes." The local client is just the first consumer. Each `MemexInstance` with a token becomes a SignalR participant connection; once connected, the **remote mesh can address this one** (render its layout areas, run scripts, message it — the control plane). See [SignalR Mesh Participant](/Doc/Architecture/SignalRMeshParticipant).

Two planes, kept separate:

| Plane | Mechanism |
|---|---|
| **Control** — operate a mesh from another | the SignalR **participant** connection (the connected mesh is addressable) |
| **Data** — copy a subtree between meshes | [Cross-Instance Mirror](/Doc/Architecture/CrossInstanceMirror) (`mirror` push/pull) or ZIP import/export |

> ⚠️ **Transport limit (today): the SignalR client is single-remote.** `UseSignalRClient` registers one `HubConnection` and the route resolves that one for every target. "Connect *any number* of meshes" needs a connection registry keyed by target + route-by-target + a **runtime** `ConnectToMesh(hub, url, token)` (the body of `CreateHubConnectionAsync`, exposed so step 4 can connect *after* the mesh is up). Until then, one remote per client.

## Platform matrix — what runs where

The client ships the **same** mesh setup everywhere; only the JIT-dependent features degrade:

| | Windows | macOS (Mac Catalyst) | Android | iOS |
|---|---|---|---|---|
| Local mesh on SQLite, CRUD, query, content | ✅ | ✅ | ✅ | ✅ |
| **Dynamic node types / interactive markdown** (Roslyn) | ✅ | ✅ (macOS allows JIT; needs `com.apple.security.cs.allow-jit`) | ✅ mostly | ❌ no JIT |
| Local Postgres | n/a (SQLite) | n/a | n/a | ❌ no fork |

**iOS is the only platform that loses Roslyn features**, and at runtime — not build. `AddGraph()` pulls `Microsoft.CodeAnalysis`, which inflates the iOS AOT bundle. The future iOS cleanup is a **narrow split**: extract the 6 compilation files in `MeshWeaver.Graph/Configuration/` (`CompilationInputs`, `MeshNodeCompilationService`, `MeshNodeLanguageService`, `ScriptCompilationService`, `SourceGeneratorLoader`, `SpeculativeCompilation`) + the Kernel/Roslyn refs into a `MeshWeaver.Graph.Compilation` assembly behind an `INodeTypeCompiler` abstraction, registered only where JIT exists — **not** all of Graph (its types/icons/layout/data-source wiring are Roslyn-free).

Threading note: iOS forbids `fork()` (→ no Postgres) and JIT (→ no Roslyn), but **threads are fine** — the actor hubs, `IIoPool`, and Rx all run. See [Controlled IO Pooling](/Doc/Architecture/ControlledIoPooling) and [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls).

## See also

- [SignalR Mesh Participant](/Doc/Architecture/SignalRMeshParticipant) — the transport this builds on.
- [Cross-Instance Mirror](/Doc/Architecture/CrossInstanceMirror) — the data plane.
- [Data Binding in a MAUI Client](/Doc/GUI/DataBindingMaui) — config/UI as mesh nodes on the client.
- The `/maui` and `/layout-area` skills — how to build features in the client and UI the implementation-independent way.

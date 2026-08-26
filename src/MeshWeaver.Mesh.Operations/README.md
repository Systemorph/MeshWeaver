# MeshWeaver.Mesh.Operations

`MeshOperations` — the mesh's get / search / create / update / patch / edit-content / move / copy /
recycle / export / upload surface, expressed once so every transport reads and writes the mesh the
same way.

## What consumes it

| Consumer | Surface |
|---|---|
| `Memex.Portal.Shared/Api/MeshApiEndpoints.cs` | the portal's REST API |
| `Memex.LocalMesh/LocalMeshApiEndpoints.cs` | the local-mesh sidecar's REST API |
| `MeshWeaver.AI` (`MeshPlugin`, `CollaborationPlugin`, `LspPlugin`, …) | the agent + MCP tool surface |

Those three are transports over ONE implementation on purpose: an agent tool and an HTTP client
that disagree about what `patch` does are two different products.

## Why it is its own assembly

It used to live in `MeshWeaver.AI`, which made the portal's REST API compile against the AI engine.
It is not AI — its only agent-shaped member was `ResolveContextPath(IAgentChat, …)`, and that took
nothing off the chat but a context string. The signature is now
`ResolveContextPath(string? contextPath, string path)`, and `MeshWeaver.AI.AgentChatPaths` is the one
place that reads the context off a chat. See Systemorph/MeshWeaver#2276 — the AI engine leaves the
platform, and this had to stop travelling with it.

## Everything returns `IObservable<T>`

Never `Task<T>`. The mesh is an actor-hub system: `await` on hub-backed work deadlocks the
single-threaded action block. Callers subscribe (`.Subscribe(onNext, onError)`) or bridge at an
external boundary (`.FirstAsync().ToTask()` in an MCP/REST adapter) — never inside hub flow. See
`Doc/Architecture/AsynchronousCalls`.

## Reading a node that may not be there

`NodeReadOutcome` keeps three answers apart — **Found**, **Absent** (a definitive negative, the only
one that may be reported as `"Not found: …"`), and **Unavailable** (no answer was reached). A caller
that collapses them into one `null` reports "not found" for a read that simply timed out.

## 🚨 The namespace is `MeshWeaver.AI`, and it may never change

The assembly moved. The **names did not, and cannot** — they are a binary contract.

A module is a plain assembly binding platform types by simple assembly name, gated on a **semver
floor and never MVID equality**, precisely so that "a landed module keeps loading across ordinary
platform updates" (`Doc/Architecture/Modules` → the skip rules). That promise is about BINARY
compatibility, and a module's IL does not hold a `using`; it holds

```
TypeRef  MeshWeaver.AI.MeshOperations     scope: AssemblyRef MeshWeaver.AI
```

so the full type name **and** the assembly that carries it are both part of it. The first cut of this
move renamed the namespace to `MeshWeaver.Mesh` and left nothing behind, and when the platform rolled,
every MCP tool call in production died in the `McpMeshPlugin` constructor with
`TypeLoadException: Could not load type 'MeshWeaver.AI.MeshOperations'` — the whole `/mcp` surface,
for every external client of the deployment (Systemorph/MeshWeaver#2370).

The move survives because `MeshWeaver.AI` leaves **type forwarders** (`src/MeshWeaver.AI/TypeForwards.cs`)
for `MeshOperations`, `MeshExportManifest`, `MeshExportFileEntry` and `NodeReadOutcome`: the CLR
resolves the old TypeRef through this assembly, yielding ONE type identity rather than a shim. **A
forwarder cannot rename**, which is why these types keep `namespace MeshWeaver.AI` in an assembly
that is not AI, and why `NodeReadOutcome` kept its original name instead of the `NodeReadResult` the
first cut chose. `MovedTypeBinaryContractTest` fails if either drifts, and
`scripts/check-type-forwards.py` refuses the next such move repo-wide.

**Source compatibility is not the question.** The first cut was verified by BUILDING the plugins
repo's `MeshWeaver.Mcp` against the branch — 0 errors — and that build proved nothing about the
module that was already published. `landed-modules-gate` has the same blind spot by construction: it
compiles module SOURCE.

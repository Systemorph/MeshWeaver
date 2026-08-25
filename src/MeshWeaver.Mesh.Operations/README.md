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

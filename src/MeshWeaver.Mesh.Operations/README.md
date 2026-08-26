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

`NodeReadResult` keeps three answers apart — **Found**, **Absent** (a definitive negative, the only
one that may be reported as `"Not found: …"`), and **Unavailable** (no answer was reached). A caller
that collapses them into one `null` reports "not found" for a read that simply timed out.

## The namespace is `MeshWeaver.Mesh`, and that is deliberate

The assembly is new; the namespace is not. Plugin source is type-checked by two gates against two
different frameworks — `compile-check.py --image` against core `main`, and the pack lane against the
newest RELEASED framework (rc7, cut 2026-08-22). A brand-new namespace would resolve on neither
until a release moved the floor, so every plugin using these types would break on the plugins repo's
MAIN rather than on the PR that caused it.

`MeshWeaver.Mesh` exists in rc7, so the move needs no cross-repo coordination at all: the one real
consumer, `MeshWeaver.Mcp`, already carries `using MeshWeaver.Mesh;` and reaches this assembly
transitively through its `MeshWeaver.AI` reference. Verified by building it against this branch — 0
errors, no plugin change. `NodeReadResult` carries its new name for the same reason: at
`MeshWeaver.Mesh.NodeReadOutcome` it would have collided with the read-classifier type already
there.

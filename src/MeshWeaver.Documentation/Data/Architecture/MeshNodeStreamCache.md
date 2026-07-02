---
Name: The MeshNode Stream Cache — One Handle per Path, One Cache per Silo
Category: Architecture
Description: The process-wide IMeshNodeStreamCache behind GetMeshNodeStream — one shared handle per path per silo, serialized writes, access-gated reads, and the storm breaker for absent nodes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/></svg>
---

# The MeshNode Stream Cache

Every call to `workspace.GetMeshNodeStream(path)` / `Hub.GetMeshNodeStream(path)` resolves the same thing: the **`IMeshNodeStreamCache`** — a singleton that lives once **per silo** and holds **one shared stream handle per node path**. Whatever runs inside that silo — per-node hubs, layout areas, Blazor views, agents, compile activities, routing — reads and writes any node through the same handle. That is the whole trick: *everything in the silo has easy, cheap, coherent access to every node.*

```csharp
// Read — live, authoritative, shared:
Hub.GetMeshNodeStream(path).Subscribe(node => ...);

// Write — same handle; cold until Subscribe:
Hub.GetMeshNodeStream(path)
    .Update(node => node with { Content = ... })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
```

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 430" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif">
  <defs>
    <marker id="snc-arr" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#90a4ae"/></marker>
    <marker id="snc-blue" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#1e88e5"/></marker>
    <marker id="snc-orange" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" fill="#f57c00"/></marker>
  </defs>
  <rect x="10" y="10" width="500" height="410" rx="14" fill="none" stroke="#5c6bc0" stroke-width="1.5" stroke-dasharray="6 4"/>
  <text x="30" y="36" font-size="13" font-weight="bold" fill="#5c6bc0">Silo A (one process)</text>
  <rect x="30" y="56" width="130" height="44" rx="8" fill="#43a047"/>
  <text x="95" y="74" text-anchor="middle" font-size="11" fill="#fff" font-weight="bold">Blazor view</text>
  <text x="95" y="90" text-anchor="middle" font-size="10" fill="#c8e6c9">Hub.GetMeshNodeStream</text>
  <rect x="30" y="116" width="130" height="44" rx="8" fill="#43a047"/>
  <text x="95" y="134" text-anchor="middle" font-size="11" fill="#fff" font-weight="bold">Thread hub</text>
  <text x="95" y="150" text-anchor="middle" font-size="10" fill="#c8e6c9">streaming writer</text>
  <rect x="30" y="176" width="130" height="44" rx="8" fill="#43a047"/>
  <text x="95" y="194" text-anchor="middle" font-size="11" fill="#fff" font-weight="bold">Agent / activity</text>
  <text x="95" y="210" text-anchor="middle" font-size="10" fill="#c8e6c9">terminal status write</text>
  <rect x="30" y="236" width="130" height="44" rx="8" fill="#43a047"/>
  <text x="95" y="254" text-anchor="middle" font-size="11" fill="#fff" font-weight="bold">Routing / queries</text>
  <text x="95" y="270" text-anchor="middle" font-size="10" fill="#c8e6c9">path resolution, warm-up</text>
  <rect x="230" y="100" width="250" height="200" rx="12" fill="#0d47a1" stroke="#1e88e5" stroke-width="2"/>
  <text x="355" y="126" text-anchor="middle" font-size="13" font-weight="bold" fill="#fff">IMeshNodeStreamCache</text>
  <text x="355" y="143" text-anchor="middle" font-size="10" fill="#90caf9">singleton — one per silo</text>
  <rect x="250" y="156" width="210" height="30" rx="6" fill="#1565c0"/>
  <text x="355" y="176" text-anchor="middle" font-size="10" fill="#fff">"acme/Story"  →  shared handle</text>
  <rect x="250" y="192" width="210" height="30" rx="6" fill="#1565c0"/>
  <text x="355" y="212" text-anchor="middle" font-size="10" fill="#fff">"rbuergi/_Thread/chat-1"  →  shared handle</text>
  <rect x="250" y="228" width="210" height="30" rx="6" fill="#1565c0"/>
  <text x="355" y="248" text-anchor="middle" font-size="10" fill="#fff">"Doc/Architecture/…"  →  shared handle</text>
  <text x="355" y="284" text-anchor="middle" font-size="10" fill="#90caf9" font-style="italic">read + write share one upstream per path</text>
  <line x1="160" y1="78" x2="226" y2="140" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#snc-arr)"/>
  <line x1="160" y1="138" x2="226" y2="170" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#snc-arr)"/>
  <line x1="160" y1="198" x2="226" y2="210" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#snc-arr)"/>
  <line x1="160" y1="258" x2="226" y2="246" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#snc-arr)"/>
  <rect x="560" y="120" width="180" height="160" rx="12" fill="#bf360c" stroke="#f57c00" stroke-width="1.5"/>
  <text x="650" y="146" text-anchor="middle" font-size="12" font-weight="bold" fill="#fff">Owning per-node hub</text>
  <text x="650" y="164" text-anchor="middle" font-size="10" fill="#ffcc80">single-threaded actor</text>
  <text x="650" y="192" text-anchor="middle" font-size="10" fill="#ffe0b2">applies RFC 7396</text>
  <text x="650" y="207" text-anchor="middle" font-size="10" fill="#ffe0b2">merge patches in order</text>
  <text x="650" y="230" text-anchor="middle" font-size="10" fill="#ffe0b2">validates · persists</text>
  <text x="650" y="245" text-anchor="middle" font-size="10" fill="#ffe0b2">broadcasts to all silos</text>
  <line x1="480" y1="180" x2="556" y2="180" stroke="#f57c00" stroke-width="2" marker-end="url(#snc-orange)"/>
  <text x="518" y="172" text-anchor="middle" font-size="9" fill="#ffb74d">merge patch</text>
  <line x1="556" y1="215" x2="480" y2="215" stroke="#1e88e5" stroke-width="2" marker-end="url(#snc-blue)"/>
  <text x="518" y="232" text-anchor="middle" font-size="9" fill="#64b5f6">sync echo</text>
  <rect x="30" y="330" width="450" height="74" rx="10" fill="none" stroke="#5c6bc0" stroke-opacity=".6" stroke-width="1.2" stroke-dasharray="6 4"/>
  <text x="50" y="352" font-size="11" font-weight="bold" fill="#5c6bc0" fill-opacity=".85">Silo B — its own cache, same handles, same owner</text>
  <text x="50" y="372" font-size="10" fill="currentColor" fill-opacity=".6">Each silo caches independently; consistency comes from the single</text>
  <text x="50" y="387" font-size="10" fill="currentColor" fill-opacity=".6">owning hub serialising every silo's patches and echoing the result back.</text>
  <line x1="480" y1="360" x2="600" y2="288" stroke="#f57c00" stroke-width="1.5" stroke-dasharray="4 3" marker-end="url(#snc-orange)"/>
</svg>

*Every consumer in a silo shares one handle per path; cross-silo coherence comes from the owning hub serialising all writers and broadcasting the reconciled state.*

---

## Why a cache at all

A node stream is a subscription to the node's **owning hub** (`SubscribeRequest` → initial frame → live patches). If every view, agent, and handler opened its own subscription, a thread with 30 visible messages would cost 30 upstream subscriptions *per reader* — and a write through one private stream would be invisible to readers holding another. Both problems disappear when there is exactly **one** handle per path:

| Property | What it buys you |
|---|---|
| **One upstream subscription per path** | N views of the same node = 1 `SubscribeRequest`, not N. Subscribing is cheap enough to use everywhere — including one-shot reads (`.Where(n => n is not null).Take(1).Timeout(...)` completes *your* subscription; the handle stays alive for everyone else). |
| **Write-read coherence** | Reads (`Subscribe`) and writes (`.Update(...)`) share the same underlying stream, so a write is always observed — in order — by every reader in the silo. |
| **Owner as the single serializer** | Cross-hub writes ship an RFC 7396 JSON-merge patch to the owning hub, whose single-threaded action block applies every silo's patches in order. Concurrent writers touching different fields both land; there is no last-write-wins on the whole node. |

## One cache per silo

The cache is registered as a **singleton on the mesh hub's service provider** — its lifetime *is* the silo's lifetime. Every hub hosted in the silo (grains in Orleans mode, hosted hubs in the monolith) resolves the same instance:

```csharp
var cache = hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
// …but you rarely touch it directly — GetMeshNodeStream(path) is the API:
workspace.GetMeshNodeStream(path);   // routes through the cache
```

In a multi-silo cluster each silo holds its own cache with its own handles. That is safe by construction: handles are *mirrors*, and only the **owning hub** (wherever it is activated) mutates authoritative state. A silo's local handle ships patches *to* the owner and receives the reconciled echoes *from* it — so two silos never disagree for longer than one round-trip.

## Reads — upstream under System, subscribers gated individually

There are **two identities on the read path, and they must never be mixed**:

**1. The shared upstream ALWAYS opens under System — never a user.** The single `SubscribeRequest` per path is infrastructure: routing, NodeType activation, path-resolution, satellite enumeration and every view read through it, and none of them is attributable to a particular user. So the cache opens that upstream under a **system identity** and keeps it alive for the entry's lifetime. It MUST NOT capture the identity of whoever happened to trigger the first read.

> 🚨 **A leaked user identity on the upstream wedges the node for everyone.** If an ambient `AccessContext` survives onto the upstream open (or onto a per-path sync hub's `BuildupAction`), RLS evaluates *that* user against the node. If the user lacks `Read`, the read throws `UnauthorizedAccessException` — and because it faults the **shared** stream / sync hub (not just that one subscriber), the hub goes to a **FAILED** state and the node wedges for **everyone, including its legitimate owner**. This is the 2026-06-23 production symptom: a co-active admin's MCP session leaked the admin's identity onto the sync hub for another user's `{user}/_UserActivity/{user}` path; RLS denied the admin, the hub deferred its `SubscribeRequest` >30 s and FAILED, and that user's activity page rendered nothing until a restart. **The fix is the rule above: the upstream / sync-hub `BuildupAction` opens under System regardless of the ambient context — `ImpersonateAsSystem`, not whatever is on `AccessService.Context`.**

**2. Each subscriber is gated by ITS OWN `Read`, before the stream is returned.** At the `GetStream` seam the cache evaluates the *current subscriber's* effective permissions on the path locally (`PermissionEvaluator` scope walk — "who can read the main node can read its satellites") and returns the shared system stream only if the result carries `Read`. A subscriber that lacks `Read` gets an `UnauthorizedAccessException` on **its own** subscription — this denial is per-subscriber and **must not fault the shared upstream**. Per-`(path, user)` results cache for 30 s. Hub principals (`sync/`, `mesh/`, `node/`, …) and NodeType-definition paths are **not** users — they fall through to the system upstream rather than being gated (evaluating a hub address yields `Permission.None`, which would otherwise throw a spurious "user 'sync/…' lacks Read").

**3. Writes are validated in the owning hub, not here.** The cache never gates writes. A `.Update(...)` ships a merge patch to the owner, whose `RlsNodeValidator` / `[RequiresPermission(Update)]` pipeline checks the **writer's** `Update` permission on its own single-threaded action block. Read-gating at the cache seam + write-validation at the owner are the two halves of access control on a node stream.

## Writes — a serial queue per path

`handle.Update(fn)` is **cold**: nothing happens until `Subscribe`. On subscribe, the write enters the path's **serial update queue**:

1. Your lambda runs against the **freshest** node state (the previous write's echo has already landed on the shared stream).
2. The handle diffs `current` vs `fn(current)` and ships only the JSON-merge patch.
3. The owner applies it on its action block and broadcasts; your observable completes with the result.

The queue exists because RFC 7396 merges JSON *objects* key-by-key but **replaces arrays wholesale** — two concurrent writers appending to the same `ImmutableList` from the same snapshot would each ship a full-array replacement and the owner would keep only the last one. Serialising per path makes every lambda see its predecessor's result, so list appends compose instead of clobbering. A stuck owner response can't starve the queue: it advances on a bounded signal while the in-flight write keeps waiting for its real terminal.

```csharp
// Three rapid submits — all three message ids land, in order:
hub.SubmitMessage(threadPath, "first");
hub.SubmitMessage(threadPath, "second");
hub.SubmitMessage(threadPath, "third");
```

## Idle release — quiet paths give their upstream back

A read entry does **not** live for the process lifetime. Like the write-side serial
queues (10-minute sliding expiration), the read cache runs an **idle sweep**: an entry
whose shared stream has had **no live subscriber and no read/write hit for the idle
window** (default 10 minutes; `MeshNodeStreamCacheOptions`) is released — its upstream
`SubscribeRequest` is closed (the owner-side mirror unsubscribes and the 45s sync-stream
heartbeat dies) and the entry is dropped. The **next read transparently re-creates it**,
exactly like a write after write-queue eviction — invisible to callers.

Two hard guarantees:

- **A stream with a live subscriber is never released.** Every subscription registers on
  the entry's refcount; the sweep's evict decision is atomic against subscriber
  attach/detach, and the idle clock restarts at the *last unsubscribe*.
- **The sweep only ever closes.** It never re-subscribes anything (the 2026-06-08 rule);
  re-opening is always driven by the next natural read.

Without this, every path ever read — GUI navigation, per-URL path resolution, routing,
NodeType activation, MCP get/search, synced-query grain warming — leaked a
permanently-connected upstream stream (~1,650 live streams / 37 heartbeats-per-second
measured on a long-lived portal).

## The storm breaker — absent nodes can't melt the silo

A read whose owner answers *NotFound* is cached as a **negative entry** with exponential backoff (2 s doubling up to 5 min). While the window is open, re-subscribing to that path replays the cached failure instead of re-opening an upstream subscription — so a loop that keeps re-reading an absent node cannot hammer the routing layer. The entry simply **expires** (the next natural read re-probes once; a successful read clears it immediately) — there is no timer that re-subscribes on its own.

The primary rule still stands: **optional / maybe-absent nodes are read via a query** (empty result on absence), never by pointing an exact-path stream at them. The breaker is the backstop, not the pattern.

## 🚨 Never go around the cache

An ad-hoc `workspace.GetRemoteStream<MeshNode, …>(addr, …)` opens a **separate** stream instance. Writes through it are invisible to every reader on the cached handle (and vice versa) — this exact bug once made compile results never land on a NodeType's node. The discouraged forms log a warning; framework plumbing that legitimately needs them uses an internal unchecked overload.

| ❌ Don't | ✅ Do |
|---|---|
| `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, …)` in app/view code | `Hub.GetMeshNodeStream(path)` |
| A second handle "just for writing" | `.Update(...)` on the same handle you read from |
| Re-reading an absent optional node in a loop | Read optional nodes via a query (empty-on-absent) |
| Forgetting to `Subscribe` an `Update` | Always `.Subscribe(_ => { }, ex => log…)` — the write is cold, and an unsubscribed handle logs on the `MeshWeaver.Mesh.RequireSubscribe` channel |

## Cross-references

- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — when to stream vs. query.
- [Request via Stream Update](/Doc/Architecture/RequestViaStreamUpdate) — building control planes on `.Update` + watchers.
- [Thread Execution Streaming](/Doc/Architecture/ThreadExecutionStreaming) — the canonical writer/renderer pair on one handle.
- [Data Binding](/Doc/GUI/DataBinding) — the Blazor side of the same handle.
- Implementation: `src/MeshWeaver.Hosting/MeshNodeStreamCache.cs` · contract: `src/MeshWeaver.Mesh.Contract` (`IMeshNodeStreamCache`, `MeshNodeStreamHandle`).

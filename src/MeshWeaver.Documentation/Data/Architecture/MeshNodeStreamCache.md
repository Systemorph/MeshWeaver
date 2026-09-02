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

**1. The shared upstream ALWAYS opens under the cache's own infrastructure identity — never a user.** The single `SubscribeRequest` per path is infrastructure: routing, NodeType activation, path-resolution, satellite enumeration and every view read through it, and none of them is attributable to a particular user. So the cache opens that upstream under a **dedicated identity** — `MeshNodeCacheIdentity` (`cache/mesh-node-cache`), applied with `accessService.SwitchAccessContext(...)` — and keeps it alive for the entry's lifetime. It MUST NOT capture the identity of whoever happened to trigger the first read.

**Why a dedicated identity rather than `ImpersonateAsSystem`.** `system-security` is granted `Permission.All` unconditionally; the hydration path needs exactly `Read`. `PermissionEvaluator.GetEffectivePermissions` therefore short-circuits `cache/mesh-node-cache` to **`Permission.Read` and nothing else**, so the bypass is as narrow as the job (`MeshWeaver.Security.Test/MeshNodeCacheIdentityTest` pins that writes under it are denied). `ImpersonateAsHub(meshHub)` is also wrong — it stamps `mesh/{guid}`, a principal no `AccessAssignment` grants, so owners' RLS denies it. (The **synced-query** upstream in the same cache is the one place that does use `ImpersonateAsSystem`: the cache hub is declared `WithPostingIdentity(System)`, and using the Read-only identity there diverged from the hub's posting identity and produced a `sync/<id>` DeliveryFailure storm.)

> 🚨 **A leaked user identity on the upstream wedges the node for everyone.** If an ambient `AccessContext` survives onto the upstream open (or onto a per-path sync hub's `BuildupAction`), RLS evaluates *that* user against the node. If the user lacks `Read`, the read throws `UnauthorizedAccessException` — and because it faults the **shared** stream / sync hub (not just that one subscriber), the hub goes to a **FAILED** state and the node wedges for **everyone, including its legitimate owner**. This is the 2026-06-23 production symptom: a co-active admin's MCP session leaked the admin's identity onto the sync hub for another user's `{user}/_UserActivity/{user}` path; RLS denied the admin, the hub deferred its `SubscribeRequest` >30 s and FAILED, and that user's activity page rendered nothing until a restart. **The fix is the rule above: the upstream / sync-hub `BuildupAction` opens under an explicit infrastructure identity regardless of the ambient context — never whatever happens to be on `AccessService.Context`.**

**2. Each subscriber is gated by ITS OWN `Read`, before the stream is returned.** At the `GetStream` seam the cache evaluates the *current subscriber's* effective permissions on the path locally (`hub.GetEffectivePermissions` → `PermissionEvaluator` scope walk — "who can read the main node can read its satellites"; **no round-trip to the leaf path's own hub**, which is what used to wedge a satellite/cell sub-path that owns no hub) and returns the shared upstream only if the result carries `Read`. A subscriber that lacks `Read` gets an `UnauthorizedAccessException` on **its own** subscription — this denial is per-subscriber and **must not fault the shared upstream**. Per-`(path, user)` results cache for 30 s. Hub principals (`sync/`, `mesh/`, `node/`, …) and NodeType-definition paths are **not** users — they fall through to the system upstream rather than being gated (evaluating a hub address yields `Permission.None`, which would otherwise throw a spurious "user 'sync/…' lacks Read").

**3. Writes are validated in the owning hub, not here.** The cache never gates writes. A `.Update(...)` ships a merge patch to the owner, whose `RlsNodeValidator` / `[RequiresPermission(Update)]` pipeline checks the **writer's** `Update` permission on its own single-threaded action block. Read-gating at the cache seam + write-validation at the owner are the two halves of access control on a node stream.

## Writes — a serial queue per path

`handle.Update(fn)` is **cold**: nothing happens until `Subscribe`. On subscribe, the write enters the path's **serial update queue**:

1. Your lambda runs against the **freshest** node state — the previous write's result, whether or not its echo has come back yet (see below).
2. The handle diffs `current` vs `fn(current)` and ships only the JSON-merge patch, **plus the base values it diffed against**.
3. The owner applies it on its action block and broadcasts; your observable completes with the result.

The queue exists because RFC 7396 merges JSON *objects* key-by-key but **replaces arrays wholesale** — two concurrent writers appending to the same `ImmutableList` from the same snapshot would each ship a full-array replacement and the owner would keep only the last one. Serialising per path makes every lambda see its predecessor's result, so list appends compose instead of clobbering. A stuck owner response can't starve the queue: it advances on a bounded signal while the in-flight write keeps waiting for its real terminal.

```csharp
// Three rapid submits — all three message ids land, in order:
hub.SubmitMessage(threadPath, "first");
hub.SubmitMessage(threadPath, "second");
hub.SubmitMessage(threadPath, "third");
```

### 🚨 "Freshest" means the predecessor's RESULT, not the mirror

The queue advances on the **hand-off** — the owner's ack (early or late), a no-op, or a terminal error — and deliberately **never waits for the echo**. So step 1 cannot mean "read the mirror": under load the mirror is still showing the node as it was *before* the write that just released the slot. This page used to say the echo "has already landed", and nothing delivered that.

The distinction is not academic. The base values shipped in step 2 are how the owner detects a stale/reordered cross-hub write: a leaf whose live value has moved past the writer's base is REFUSED (`MeshNodePatchMerge`). A successor that diffed against the un-echoed mirror therefore shipped a base **it had itself superseded one write earlier**, and the owner refused the conflicting leaves of a write that nothing was concurrent with — while the write's *other* leaves applied and the whole thing still acked `Success`. Issues #2305 / #2291: an agent response cell that reached `Status = Completed` with `Summary` holding the answer and `Text` still reading `"Generating response..."`, because only `Text` conflicted. Generalised: a streaming cell's text froze at the first chunk that landed, for as long as the echo lagged the write rate.

So the queue **hands each write the node the owner acknowledged taking from its predecessor**, and the write diffs against that. The moment the mirror carries anything newer — the echo, or a genuinely different writer's commit, since the owner mints `Version + 1` on every applied change — the mirror wins again and real cross-mirror conflicts are detected exactly as before. The hand-forward is one-shot (it can inform only the next write), and a `Conflict` re-attempt still re-reads the owner's state. `[UpdateRemote] SELF_REBASE` in the debug log is a write saying it took this path.

🚨 **Acknowledged, not merely computed.** A base taken from the *optimistic* snapshot — a write whose owner ack never arrived — is self-perpetuating, because a write that did not land mints no version and so nothing ever corrects it: a caller that retries the same write then diffs against its own unlanded value, computes an **empty** patch, and skips the write **silently, forever**. `TwoSiloRecycleConvergenceTest` catches exactly that (the post-recycle write retried past a disposing owner and the store never advanced). So a rejected or retried write publishes nothing and its successor reads the mirror — the pre-existing behaviour, which is the correct direction to degrade in.

### 🚨 The queue slot is released by the HAND-OFF, never by the optimistic emit (#2346)

The paragraph above is the *whole* rule only if the successor cannot start before the hand-off is decided — and for a while it could. `UpdateRemote` bounds its wait for the owner's `PatchDataResponse` at `UpdateResponseWaitBound` (~2 s) and then emits the optimistic snapshot so the caller is never stalled; that emission was also what released the queue slot. On a busy owner — a loaded CI runner, a grain still activating, a per-node hub mid-round — the ack simply does not arrive inside that window, so the successor started with **no hand-off at all** and fell straight back to the un-echoed mirror. The fix above therefore worked exactly when it was not needed and did nothing when it was, and the `Status = Completed` / `Text = "Generating response..."` cell came back on a tree that already contained it (#2346, `OrleansAutoExecuteTest`).

Two things follow, and both are now true:

- **A LATE ack publishes just like an early one.** It is the same owner ack; arriving after the caller's optimistic emit changes nothing about what the owner took. It is dispatched through `LatePatchResponseRegistry`, which was already armed for the late-NACK re-enqueue.
- **The slot is released by the hand-off, not by the caller's terminal.** `onLocalState` takes `MeshNode?`: a node publishes a base *and* releases the slot; `null` releases it with nothing to publish (a re-enqueued attempt, a terminal late NACK — verdicts that will never produce a base, and which must not make the successor sit out `QueueAdvanceBound`). An `Overwrite` asserts the whole node and leaves no base, so its own terminal is its hand-off.

The queue slot and the caller's terminal are **independent signals**, and that independence is load-bearing twice: it is what let #2346 fix the successor's base without touching the caller, and it is what let #2661 (below) fix the caller without stalling the successor.

`QueueAdvanceBound` remains the backstop for an owner that never answers at all. Taking it is a real loss of the invariant, so it is no longer silent: `[UpdateQueue] ADVANCE_WITHOUT_HANDOFF` says the successor is about to diff against a mirror that may not carry its predecessor, and the owner's `[MergeGuard]` warning is what you will see if a leaf is then refused.

### 🚨 "Saved" means the owner COMMITTED — a bound expiring is not a verdict (#2661)

The optimistic emit above was not only the wrong base for the successor. It was also the **caller's success terminal**, and that made the write path fail **open**: `UpdateRemote` waited `UpdateResponseWaitBound` (~2 s) for the owner's `PatchDataResponse`, and on expiry emitted the locally-computed snapshot and **completed the caller as a success**. A bound elapsing says nothing about whether the owner took the patch.

The refusal case is the one that hurt. An RLS denial is **not** a `PatchDataResponse` — `AccessControlPipeline` posts a `DeliveryFailure{ErrorType.Unauthorized}` correlated to the patch — and `LatePatchResponseRegistry` watched only `PatchDataResponse`. So a denial that lost the race against that bound reached **nothing at all**: the caller's `Observe` callback was already gone, `MessageHub.HandleCallbacks` logged *"No subject found for response message"* and marked the delivery processed, and the caller kept "saved" for a write the owner had refused. The data was safe (the owner denied; the node was unchanged) — the *report* was wrong, which is the silent-failure shape: a UI renders the optimistic value and a workflow proceeds on a write that never landed.

The rule now matches what `add` and `delete` have always done. Those issue a `DataChangeRequest` and answer `Committed`-or-fail off the real verdict; `update` ships a `PatchDataRequest` instead — a cross-hub writer holds only a **mirror**, so its own workspace's `RequestChange` would commit into the mirror and the owner would never see the write — but the owner's ack is if anything **stronger** than `Committed`: it is posted off an identity-gated post-commit emission plus `IPostCommitFlush`'s durable flush. So:

- **The caller's terminal is the owner's verdict, wherever it arrives.** Expiry of `UpdateResponseWaitBound` emits nothing and completes nothing; it only hands the wait from the pending `hub.Observe` callback (whose lifetime is bounded by the hub's quiescing budget) to `LatePatchResponseRegistry`. A **late ack** completes the caller as a success; a **late NACK** faults it, or chains the re-enqueued attempt's verdict to it.
- **`LatePatchResponseRegistry` now watches `DeliveryFailure` too** (`DispatchFailure`, dispatched by a second handler on the cache hub). A late `Unauthorized` faults the caller with the same `UnauthorizedAccessException` the early-denial arm raises.
- **Silence past `LateResponseWatchBound` (30 s) faults, it does not succeed.** That window is built to dominate every owner-side terminal path — the identity-gated ack watcher's 20 s, the 10 s post-commit flush, and the disposal NACK — so a write still unanswered there has not merely found a busy owner; the owner produced no terminal at all. `[UpdateRemote] VERDICT_TIMEOUT` names it, and the error says the patch was posted and may still apply but is **not confirmed**. Completing optimistically here would be the same fail-open, one bound later.

**The cost, stated plainly:** a write to a *busy* owner now waits for that owner's queue to drain to the patch, instead of being told "saved" at ~2 s. That is deliberate — the alternative is reporting outcomes we have not got. Nothing on the successor side got slower: the queue slot is still released by the hand-off, exactly as the section above describes.

## Big strings ship as a SPLICE, not as the whole value

Step 2 above — "ships only the JSON-merge patch" — is per *leaf*, and a changed leaf normally travels
whole. For a field that **grows one chunk at a time** that is quadratic: an agent response cell
re-sent its entire text on every `Sample(100 ms)` tick, and `ExtractBaseValues` re-sent the entire
*previous* text alongside it, so one reply cost `O(ticks × final length)` — **3.8 MB measured for a
20 kB answer**, all of it Large-Object-Heap traffic on the writer and the owner both.

So a changed **string** leaf at least `PatchStringSplice.MinSpliceLength` (1 kB) long, whose change
is smaller than the value, is encoded as a splice instead:

| where | shape |
|---|---|
| in the patch | `{ "$sd": [start, removedLength, "inserted"] }` |
| in `BaseValues`, same leaf | `{ "$sdb": [baseLength, "fingerprint"] }` |

Both halves matter. Splicing only the patch would still leave the base half re-shipping the previous
value every tick, and the round would stay `O(ticks × length)`.

**The owner applies a splice only when it can prove the offsets still address the right characters.**
The fingerprint (length + truncated SHA-256) is compared against the owner's live text:

- **match** → the owner's text *is* the text the writer diffed against, so applying the splice yields
  byte-for-byte the string a whole-value patch would have written. This is the normal path.
- **mismatch** → the owner moved on since the writer's base. The leaf is **refused**: the owner keeps
  its newer value and NACKs `Conflict`, which re-runs the writer's update lambda against the fresh
  state and re-diffs — the same self-healing route a refused scalar takes.

That refusal is the deliberate half of the trade. A splice is never applied at an offset the
fingerprint did not vouch for, so two mirrors splicing the same string concurrently cannot interleave
into corrupted text; the loser re-diffs instead. The cost is that a *disjoint* concurrent edit to a
big string, which the full-value three-way merge would have rebased and landed, now costs a round
trip. For every field this applies to — streaming cells, markdown bodies, prerendered html — that is
a trade of a rare extra round trip against a constant, per-tick, per-viewer megabyte.

Below 1 kB, and for arrays and scalars, nothing changes: the whole value and the whole base still
travel, and `MeshNodePatchMerge` resolves them exactly as before (arrays in particular *need* their
full base — `MergeArray` consumes it to tell a writer's removal from an element the owner dropped).

**Rolling deploys.** The marker lives *inside* the patch, so during the minute or two a rollout has
old and new pods coexisting, a new writer can send a splice to a per-node hub still running the old
image. That owner does not decode `$sd`, merges the marker object into a string-typed field, and the
merged node then **fails to deserialize** — the write is NACKed `Deserialization` and never commits.
Loud and non-destructive, and it self-heals: the mirror never advanced, so the next splice is
computed against the same base and lands the whole missing span once the owner is upgraded.

That failure mode is deliberate. The obvious alternative — carrying splices in a new sibling property
the old owner would simply ignore — degrades instead into an owner that **acks a write it only
partly applied**, which is the acked-write-loss class of [#648](https://github.com/Systemorph/MeshWeaver/issues/648).
A visibly stalled field beats a silently lost one.

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

### A warm mirror is a KEEP-ALIVE, so "final" must be an event, not a wait

The ten-minute window is a *heuristic about the future*: a path touched just now is
probably about to be touched again. For a node with a **terminal lifecycle** that
heuristic is provably false, and waiting it out is not merely wasteful — it is
self-defeating. The mirror posts a `HeartBeatEvent` to its owner every 45 s
(`SyncStreamOptions.HeartbeatInterval`) *expressly to keep that hub alive*, and that
message re-arms every idle clock the platform has: the cache's own window, an Orleans
grain's collection age, `KernelContainer.DisposeOnTimeout`. So a finished activity does
not sit waiting to be reclaimed — it **prevents its own reclamation**, and it takes the
owning node hub and the `sync/` sub-hubs on both sides with it
([#1324](https://github.com/Systemorph/MeshWeaver/issues/1324)).

`IMeshNodeStreamCache.ReleaseIfUnwatched(path)` is the event-driven counterpart: the same
detach → claim → tear-down protocol as the sweep, and the same **atomic zero-subscriber
guard**, but with the wait replaced by proof from the caller. Its one caller today is
`ActivityLogAppender.Append`, which releases the path in the same write that reports a
status where `ActivityStatus.IsTerminal()` holds — an activity in that state is never
written again, by definition. A reader still watching the finished activity wins the race
and keeps its mirror; the release simply declines and the sweep remains the backstop.

🚨 It is **not** a shorter timer, and must not become one. Nothing about the idle window
changes; what changed is that a caller who *knows* the answer no longer has to wait for
the heuristic to guess it. Measured on `NodeTypeRecompileAlcLeakTest`: 6.5 → 5.0 retained
hubs per compile activity, with the `cache/`-side mirror hubs going 3 → 0.

### Once the mirror is released, the retention is BOUNDED — and the ceiling is 15 minutes

Releasing the mirror does not itself reclaim the activity's own node hub; it removes the
thing that was **stopping** the reclaim. What finishes the job is already there, in the
monolith as well as on Orleans: an **Activity** node hub is one of the few hub kinds that
carries an idle reaper of its own —

```
ActivityNodeType.CreateMeshNode        HubConfiguration = … .AddKernelSubHubHandlers()
  → KernelHubConfigurationExtensions   ParentHub.GetService<IKernelHubConfigurator>()
  → KernelContainer.ConfigureSubHub    .WithInitialization(hub => DisposeOnTimeout(hub))
  → KernelContainer.DisposeOnTimeout   one-shot Timer, KernelHubOptions.IdleDisconnectTimeout
```

registered by `KernelNodeType.AddKernel()`, which `AddGraph()` includes. The timer is
**one-shot and re-armed by every inbound message**, which is exactly why the 45 s heartbeat
above kept it at bay forever — and why, with the terminal release in place, nothing re-arms
it and the hub disposes one window later, taking its `sync/` sub-hubs with it.

So the per-compile `_Activity/compile-<ts>` residual that `NodeTypeRecompileAlcLeakTest`
counts is a **transient with a 15-minute ceiling, not a leak** — that test simply runs for
seconds and cannot watch it expire. Measured unscaled, at production timer values, with the
real 45 s heartbeat present:

| t | per-compile activity hubs | mesh total hubs |
|---:|---:|---:|
| 0 – 885 s | 15 | 51 → 47 |
| 900 s | 5 | 37 |
| **915 s** | **0** | **32** |

13 hubs reclaimed in **915 s = 15.25 min** — one idle window plus the 15 s poll granularity.
(The mesh total's earlier 51 → 47 step near t+660s is the cache's own 10-minute idle sweep,
a different reaper doing a different job.) `CompileActivityHubRetentionTest` is the
CI-affordable guard on the same property, and it measures it directly: the activity hubs
disappear after **exactly one idle window**, with nothing re-arming the timer.

🚨 That test compresses the window **and the heartbeat together**, at production's 1:20
ratio, and the pairing is load-bearing. Shortening only the window would put the 45 s
heartbeat *outside* it: the timer would fire between two heartbeats, the test would pass,
and production — where 45 s sits far inside 15 min — would still reclaim nothing. A guard
that can only pass is not a guard.

🚨 **Do not shorten `IdleDisconnectTimeout` to make a memory figure look better.** The whole
lesson of this section is that the clocks were correct and something was resetting them; a
shorter window would have hidden that defect rather than cured it.

### The idle sweep is not the only reaper — an evicted upstream dies with its last holder

The idle sweep answers *"this path went quiet"*. It cannot answer *"this write is finished
with its mirror"*, and that is the case a busy path is always in: `Workspace.EvictForPath`
retires a path's upstream on **every** change event — including the echo of the writer's own
write — so the next writer diffs against the owner's authoritative state rather than a stale
snapshot. That eviction is load-bearing; parking the retired stream until the idle sweep
happened to notice was not, and a continuously-written path never meets the sweep's
"zero subscribers **and** ten minutes untouched" condition
([#1324](https://github.com/Systemorph/MeshWeaver/issues/1324)).

Reference counting the stream's Rx subscribers cannot decide it either: the reduce chain
`JsonSynchronizationStream.CreateExternalClient` builds subscribes to the stream **itself**, so
a retired mirror sits at 2–3 subscribers forever and a "dispose at zero" rule never fires.

So holders **declare** themselves. Anything keeping a remote stream past the call that resolved
it takes a lease from `Workspace.AcquireRemoteStreamUnchecked` and disposes it when done:

| holder | lease lifetime |
|---|---|
| this cache's per-entry hydration (`CreateEntry` → `handle.Subscribe`) | the entry's whole life — it *is* the path's one live mirror |
| a cross-hub write (`MeshNodeStreamHandle.UpdateRemote` / `WriteViaSyncStream`) | the write's `Observable.Create` subscription |
| a reduce callback that hands the stream on (`MeshDataSource`, `SyncedQueryDataSourceExtensions`) | **none** — undeclared, so it keeps the conservative parking |

An **evicted** stream with no remaining declared holder is disposed at once — `UnsubscribeRequest`
reaches the owner, and both the client and owner `sync/` hubs die. A stream nobody leased is never
touched by this and is still reclaimed by the idle release above or at workspace disposal. Steady
state on a hot path is therefore ONE live mirror, not one per write.

## The storm breaker — absent nodes can't melt the silo

A read whose owner answers *NotFound* is cached as a **negative entry** with exponential backoff (2 s doubling up to 5 min). While the window is open, re-subscribing to that path replays the cached failure instead of re-opening an upstream subscription — so a loop that keeps re-reading an absent node cannot hammer the routing layer. The entry simply **expires** (the next natural read re-probes once; a successful read clears it immediately) — there is no timer that re-subscribes on its own.

The primary rule still stands: **optional / maybe-absent nodes are read via a query** (empty result on absence), never by pointing an exact-path stream at them. The breaker is the backstop, not the pattern.

A failure that is *not* a genuine missing node — a transient reactivation miss, a request timeout, a lost database connection, or anything else the classifier does not recognise — is recorded in the **transient breaker** instead. Its first `TransientGraceFailures` (3) faults open no window at all (the just-idle-page case keeps its instant re-probe); beyond the grace, re-probes back off 1 s doubling to a 60 s cap. Every fault lands in exactly one of the two breakers — there is no un-broker'd bucket where a fault can repeat unbounded.

### A faulted entry is never served twice

Both breakers *suppress* while their window is open. When no window is open, the opposite must hold: the read has to actually re-probe. `GetStreamRaw`'s third guard enforces that — an entry whose hydration terminated with an error is evicted before the read resolves, so `SharedView` opens a fresh upstream.

The eviction (`EvictFaultedEntry`) is the one teardown shared by all three re-probe triggers, and it must reach **two** layers:

1. the cache's own `Entry`, whose `Replay(1)` holds the terminal error, and
2. the cacheHub **workspace's** remote-stream cache, keyed `(owner, reference, identity)`, which holds the errored `ISynchronizationStream`.

Dropping only (1) re-creates a fresh entry over the same dead stream: no `SubscribeRequest` is posted and the reader gets the *identical exception instance* back. That was #1202 — three probes eleven minutes apart returning the byte-identical request id, a path unreadable for the pod's lifetime, and breakers whose fail counters were frozen at one because no new upstream ever ran the bookkeeping observer again.

The protocol is the idle sweep's: **detach → claim pair-exact → tear down** (a loser re-parks what it detached). Detaching first is what makes disposal safe — a concurrent read builds a new stream with a new `StreamId`, so the `UnsubscribeRequest` for the old one cannot race it. Only a **faulted** entry qualifies; a healthy live entry is never torn down, because a breaker record can outlive the fault that wrote it and severing live subscribers over a stale counter would be a regression.

🚨 The guard lives in `GetStreamRaw`, **never in `GetEntry`** — `GetEntry` is a pin-or-recreate loop, and evicting there spins forever on an entry that faults during creation. Running once per read *call* also bounds the re-probe: only a terminal entry qualifies, so an in-flight probe is shared, giving one new upstream per **fault**, not per read.

## A delete tombstone is superseded at the recreate's commit — before `Created` is published

The one read failure a reader is *designed not to retry* is the delete tombstone's verdict: `No node found at '…' — the node was deleted, so this address will not reactivate`. A hub that is going down because its node was **deleted** NACKs an abandoned delivery with that authoritative `NotFound` (instead of the transient `ShuttingDown` a recycling hub answers), and the classifier here turns it into a definitive absence — the stream terminates, the negative entry is recorded, and by contract the caller stops. The verdict is read off `RecentlyDeletedRegistry` (`IAddressTombstones`), which the **delete marks synchronously** before its response returns.

**The recreate must supersede that tombstone with the same discipline — synchronously, at the durable write, before `Created` is published (#3008).** Until it did, the only clear ran inside a *live per-node hub's* change handler: asynchronous, incidental (it needed a hub to be alive for the path at that instant — the delete had just disposed it), and conditional (a same-version recreate was skipped as a self-write echo). So after `Deleted` **and** `Created` had both been observed on the change feed, a fresh subscriber whose `SubscribeRequest` was routed to the still-disposing old hub — the hub stays resolvable until its `ShutDown` phase, potentially seconds after `DisposeHostedHubs` under load — was told, authoritatively, that a node which provably existed again would never come back.

The seam is the outermost storage decorator (`SubtreeDeletionGuardStorageAdapter`), because **every durable write crosses it**: on the write's post-commit emission it calls `RecentlyDeletedRegistry.Supersede(path, version)`, and `StorageAdapterChangeFeedExtensions` composes the `IMeshChangeFeed` publish *downstream* of that emission — so by construction `Created` never reaches a subscriber while the tombstone still reads live. Two rules keep it correct:

- **Supersede, never erase.** A superseded tombstone answers `IsDeleted` / `IsRecentlyDeleted` with `false` (the address is not gone for good; a save there is not a resurrection), but the delete stays on record for the TTL *with the version the recreate landed at*. `MeshNodeTypeSource`'s version floor recognises the recreate's rewind to `Version = 1` through `IsRecreatedAt(path, version)` — a hub whose routing-supplied own-node stream delivers the recreate *after* the commit would otherwise drop it as a stale replay.
- **Only a commit supersedes.** The `null` write sentinel (no adapter owns the path), a `WriteIfVersion` that answers `false`, and a refused write under an active subtree deletion write nothing and leave the tombstone live.

Pinned by `TombstoneSupersededBeforeCreatedTest` (the ordering, hub-free and deterministic) and `RecreateSupersedesTombstoneTest` (the end-to-end shape on a Monolith mesh).

## 🚨 Never go around the cache

An ad-hoc `workspace.GetRemoteStream<MeshNode, …>(addr, …)` would open a **separate** stream instance. Writes through it are invisible to every reader on the cached handle (and vice versa) — this exact bug once made compile results never land on a NodeType's node. So the public overloads now **throw `InvalidOperationException`** for `MeshNode` (`Workspace.ThrowIfMeshNode`); framework plumbing that legitimately needs the raw reduce uses the internal `GetRemoteStreamUnchecked` overload.

| ❌ Don't | ✅ Do |
|---|---|
| `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, …)` in app/view code (throws) | `Hub.GetMeshNodeStream(path)` |
| A second handle "just for writing" | `.Update(...)` on the same handle you read from |
| Re-reading an absent optional node in a loop | Read optional nodes via a query (empty-on-absent) |
| Forgetting to `Subscribe` an `Update` | Always `.Subscribe(_ => { }, ex => log…)` — the write is cold, and an unsubscribed handle logs on the `MeshWeaver.Mesh.RequireSubscribe` channel |

## Cross-references

- [Reading a Write Verdict](/Doc/Architecture/ReadingAWriteVerdict) — when a write here fails, which site minted the error and which hub to investigate. An owner that never answered and an owner that answered with its own timeout look identical to a user.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — when to stream vs. query.
- [Request via Stream Update](/Doc/Architecture/RequestViaStreamUpdate) — building control planes on `.Update` + watchers.
- [Thread Execution Streaming](/Doc/Architecture/ThreadExecutionStreaming) — the canonical writer/renderer pair on one handle.
- [Data Binding](/Doc/GUI/DataBinding) — the Blazor side of the same handle.
- Implementation: `src/MeshWeaver.Hosting/MeshNodeStreamCache.cs` · contract: `src/MeshWeaver.Mesh.Contract` (`IMeshNodeStreamCache`, `MeshNodeStreamHandle`).

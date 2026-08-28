---
name: mesh-data
description: 'Read and write mesh data correctly — the ONE mutation API (GetMeshNodeStream(path).Update), the CQRS read rules (never query for a single node content), the object-payload accessors (.As / .ContentAs instead of a cast), and the Postgres one-schema-per-partition layout. Use whenever code creates, reads, updates, moves or deletes a mesh node, waits for a node to reach a state, reads a payload that crossed a hub boundary, or touches partition schemas. The three bugs this prevents are all SILENT: a bespoke request/response that races the watcher and wedges a hub, a stale query answer that decides a write, and a cast that yields null because the value arrived as JSON.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /mesh-data — one write API, one read API, and never cast a payload

Three rules, and every silent data bug on this mesh is a violation of one of them:

1. **Write** through `workspace.GetMeshNodeStream(path).Update(...)` and nothing else.
2. **Read** a specific node's content from its stream, never from a query.
3. **Convert** an `object`/`Content` payload with `.As<T>()` / `.ContentAs<T>()`, never with
   `as` / `is`.

> Canonical references:
> - [MeshNodeStreamCache.md](../../../src/MeshWeaver.Documentation/Data/Architecture/MeshNodeStreamCache.md) — the handle contract: one cache per silo, one shared handle per path, serial write queue, storm breaker.
> - [RequestViaStreamUpdate.md](../../../src/MeshWeaver.Documentation/Data/Architecture/RequestViaStreamUpdate.md) — the canonical pattern + helpers (`hub.WatchControlPlane`, `hub.WatchSubmission`).
> - [ActivityControlPlane.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ActivityControlPlane.md) — `Status`/`RequestedStatus` pair, operations-as-scripts.
> - [CqrsAndContentAccess.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md) — read semantics + why a query lags.
> - [DataAccessPatterns.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DataAccessPatterns.md) · [DataBinding.md](../../../src/MeshWeaver.Documentation/Data/GUI/DataBinding.md) (the Blazor mirror).
> - `src/MeshWeaver.Mesh.Contract/ObjectAsExtensions.cs` — `As<T>` / `ContentAs<T>`.

## 1. `GetMeshNodeStream().Update()` is the ONLY mutation API

**Every mesh-node mutation goes through
`workspace.GetMeshNodeStream(path).Update(current => modified)`. There is no other mutation surface
— do NOT invent one: no `SubmitMessageRequest`-style wire messages, no completion callbacks via
`hub.Set<Action<...>>`, no bespoke `IRequest`/`IResponse` pairs for state changes. Migrate any
straggler you touch to `stream.Update`.**

**Sanctioned exceptions (NOT for state mutations):**

- `CreateNodeRequest` / `DeleteNodeRequest` / `MoveNodeRequest` — node *lifecycle* on the mesh hub.
  These route; they don't mutate node content.
- Transient queries that don't belong on any node (e.g. autocomplete completions).

**Why this unblocks tests:** every "hub becomes unresponsive after the second compile" failure
(CodeEditRecompile, NodeTypeRelease, LinkedInPullActions, ThreadAgentIntegration in CI
26036857424) traces back to bespoke request/response patterns that race the watcher → two
concurrent activities → leaked callbacks → wedged hub.

### Own node and external node — same API

`workspace.GetMeshNodeStream(path)` returns a handle that auto-dispatches:

- `path == my-hub's-address`: writes go through the local data source (`UpdateOwn`).
- `path != my-hub's-address`: writes route to the owning per-node hub via the process-wide
  `IMeshNodeStreamCache`, which opens a sync subscription + posts a JSON-merge `PatchDataRequest`
  (RFC 7396) to that hub. The owner serialises every mirror's write through its single-threaded
  action block — no race, no clobber.

```csharp
// Own node (this hub) — Update is COLD: the trailing Subscribe runs the write.
workspace.GetMeshNodeStream().Update(node => node with { Content = ... })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));

// External node (anywhere in the mesh — same API):
workspace.GetMeshNodeStream(otherPath).Update(node => node with { Content = ... })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
```

The remote variant returns the locally-computed updated snapshot optimistically; if you need the
owner's reconciled state, take the next emission off the same `GetMeshNodeStream(path)` handle.

**Eventual-consistency safe**: cross-hub `stream.Update` does NOT send the whole node back. It
diffs `current` vs `update(current)` and sends only the RFC 7396 JSON-merge patch. The owner merges
the patch against its CURRENT state, so concurrent writers from different mirrors don't clobber
each other's fields (Mirror A's `{Content: {Field1: X}}` and Mirror B's `{Content: {Field2: Y}}`
both land — never "last write wins on whole node"). Treat your `update` lambda accordingly: touch
only the fields you intend to change.

### The 3 rules this unifies

1. **Writes**: `stream.Update(current => current with { Content = ... })`. The owning hub's action
   block serialises; no race. State-machine semantics? Set a `RequestedX` field — the owning hub's
   watcher reacts (see ActivityControlPlane.md).
2. **Reads**: `workspace.GetMeshNodeStream(path)` / `Hub.GetMeshNodeStream(path)` — server-side AND
   Blazor, backed by the process-wide `IMeshNodeStreamCache` (one shared handle per path).
   `GetRemoteStream<MeshNode, …>` is framework plumbing — never use it for a node by path. Never
   query for a single node's content (stale by design — section 2).
3. **Delete the request type.** If you find yourself writing `class XxxRequest` to mutate a thread /
   message / NodeType, stop. Add a `RequestedXxx` field to the node's content and watch it from the
   owning hub.

### Observing completion

Subscribe to `workspace.GetMeshNodeStream(path)` and wait for the relevant state on the node's
`Content` (e.g. `MeshThread.Messages.Count >= 2`, `RequestedStatus = Cancelled`,
`Status = Completed`). The GUI databinds the same way; tests do too.

**Tests**: any test that posts a verb-shaped request and waits for a response shape
(`*Request → *Response`) is testing a deprecated API. Migrate to: write via `stream.Update`,
observe via `GetMeshNodeStream(path).Where(node => predicate).FirstAsync().Timeout(...)`.

**Application code uses only `stream.Update`.** Internal plumbing that `stream.Update` itself uses
(`PatchDataRequest` for cross-hub writes, `DataChangedEvent` for stream fan-out) is fine where it
already exists — but you never `hub.Post(PatchDataRequest, ...)` from application code. If you find
yourself doing that, you're bypassing the API.

### Thread submissions — the complete surface

Thread operations go through the canonical `IMessageHub` extensions in
`MeshWeaver.Plugins/src/MeshWeaver.AI/HubThreadExtensions.cs` (the AI engine lives in the plugins
repo, #2276):

```csharp
hub.StartThread(namespacePath, userText, agentName: ..., contextPath: ..., onCreated: ..., onError: ...);
hub.SubmitMessage(threadPath, userText, agentName: ..., contextPath: ...);
hub.ResubmitMessage(threadPath, userMessageId, newUserText: ...);
hub.DeleteFromMessage(threadPath, atMessageId);
hub.MarkThreadDone(threadPath, done);
hub.RecordSubmissionFailure(threadPath, userMessageId, userText, errorMessage);
```

Every method writes the thread node via
`hub.GetWorkspace().GetMeshNodeStream(threadPath).Update(...)` (or `CreateNodeRequest` for
new-thread lifecycle). The per-thread submission watcher reacts to the resulting state changes,
drains `PendingUserMessages` into `Messages`, allocates cells, and invokes
`ThreadExecution.ExecuteMessageAsync(execHub, RoundParams, AccessContext?)` **directly as a
method** — no message dispatch. It returns `IObservable<Unit>`; the watcher **subscribes** and
treats completion (gated on the terminal `Status` write) as round-done. **Tests, GUI, and agents
all call these extensions — this is the complete submission surface; there is no other entry
point.** No wrapper class, no path→id resolution, no create-or-submit logic beyond those APIs; pass
node PATHS through and let downstream load the node.

**Completion**: an agent reaching terminal state writes `Status = Completed/Cancelled/Error` to the
response cell via `PushToResponseMessage(...)` (stream.Update), AND creates a `Notification`
MeshNode satellite at `{threadPath}/_Notification/{id}` via `EmitCompletionNotification`. The
user's notification bell databinds to this — the same source the tests assert on. Query shape:
`path:{threadPath}/_Notification scope:children nodeType:Notification` (filter by nodeType for
robustness when other satellite types live under the thread).

Full reference: [ThreadOperations.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ThreadOperations.md).

### Activity operations

`src/MeshWeaver.Mesh.Contract/HubActivityExtensions.cs`:

```csharp
hub.CancelActivity(activityPath);                                  // RequestedStatus = Cancelled
hub.RequestActivityStatus(activityPath, ActivityStatus.Running);   // generic flip
```

Both write the activity node via `GetMeshNodeStream(activityPath).Update(...)`; the activity hub's
`WatchControlPlane` subscription reacts. Full reference:
[ActivityOperations.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ActivityOperations.md).

### Per-user work at logon — a `LogonAction`, never a SQL backfill

**When an EXISTING user needs something a new user gets, declare a logon action — do NOT write an
`IMigration` that loops partition schemas patching `mesh_nodes`.** `INodePostCreationHandler` fires
once at account creation and can never fire again, which is why `V29_PinDocsForExistingUsers` and
`V33_SeedChatInputForExistingUsers` exist; those raw `UPDATE`s bypass the workspace cache, run once
per DEPLOYMENT rather than per user, and only someone shipping a `DbVersion` bump can write one.

- **Two modes.** `RunOnce` (a migration; ledger = `User.CompletedLogonActions[id]`, durable and
  replicated) or `EveryLogon` (a repair that must keep catching new work — and which MUST carry a
  cheap "nothing to do" check, or it is a per-logon storm).
- **Idempotency is the effect and the ledger entry in ONE `stream.Update` patch** on the user's
  profile, with the ledger check *inside* the lambda so a rebased patch re-reads it and no-ops.
- **It runs as the USER** — `access.RunAs(identity, …)`. 🚨 Never
  `Observable.Using(() => access.ImpersonateAsSystem(), …)`: store and restore land on different
  threads and the subscriber stays latched (a ratchet-guard test fails the build at any new site).
- **Deployment-specific work is DATA** — a `LogonAction` node at `Admin/_LogonAction/{id}`. Zero
  action nodes ship, and pin targets are existence-checked, so a portal without the content pins
  nothing instead of writing a dangling path.

Full reference: [LogonActions.md](../../../src/MeshWeaver.Documentation/Data/Architecture/LogonActions.md)
· the `/logon-action` skill (a Skill node shipped by the AI engine in MeshWeaver.Plugins).

### The data-access table

Never use `IMeshStorage` or `IMeshCatalog` directly — internal infrastructure only.

| Operation | API |
|---|---|
| Read (query) | `IMeshService.Query<T>(request)` — reactive. 🚨 There is **no** `QueryAsync` on the production interface; it survives only as a test-only bridge in `MeshWeaver.Fixture`. One-shot snapshot = `.Where(c => c.ChangeType == QueryChangeType.Initial).Select(c => c.Items).FirstAsync()` |
| Read (single node) | `workspace.GetMeshNodeStream(path)` |
| Create/Delete | `meshService.CreateNode(node).Subscribe(...)` / `meshService.DeleteNode(path).Subscribe(...)` |
| Update | `workspace.GetMeshNodeStream(path).Update(current => current with { … })` |
| Move | `hub.Observe(new MoveNodeRequest(src, dst)).Subscribe(...)` |

Always `GetRequiredService<T>()` — never `GetService<T>()` + null check for required services.

Identity on writes: every framework write primitive carries the caller's `AccessContext` through
`.Subscribe()` boundaries — see [/async](../async/SKILL.md) Rule 2 for when you must re-establish it.

## 2. CQRS — never query for a single node's content

`Query`/`ObserveQuery` are eventually consistent — **stale after writes**. To read a specific node:

```csharp
// ❌ WRONG — lagged index, stale after writes
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ✅ CORRECT — authoritative, live (shared IMeshNodeStreamCache handle)
workspace.GetMeshNodeStream(path)
    .Where(node => node is not null)
    .Take(1).Timeout(TimeSpan.FromSeconds(10));
```

**Valid query uses:** listing children (`path/*`), searching by predicate, autocomplete — anywhere a
stale negative is harmless.

🚨 **Existence of a SPECIFIC path is NOT one of them** — use `GetMeshNodeStream(path)` / a direct
read. A query's negative can be minutes old, so a caller that reads it as "absent" redoes work that
already happened. This guidance used to sanction existence checks, and that was wrong in a way that
shipped: on 2026-08-25 a `search` reported two just-minted tiles missing while a direct `get`
returned both (#2229), and a create whose reply was lost plus a query that then answered "absent"
gave one caller two independent reasons to conclude nothing had happened — two mesh-wide sweeps
were armed 40 seconds apart on that basis. Existence-by-query is safe only where the write it
guards is path-deterministic and idempotent (the redundant write lands on the same path); where the
target id is minted per attempt, a stale negative produces DUPLICATE DATA, not merely duplicated
work.

**Wrong:** reading content by exact path, reading state before a write, polling for job completion,
deciding create-or-skip for a known path.

`GetMeshNodeStream(path)` + `Where(...).Take(1)` is also the right primitive for **waiting for work
to finish** — for a node that **exists**.

### A node that may NOT EXIST YET — the one case needing both halves

Neither half is enough on its own, and each half alone is a defect that has shipped. Do not pick a
horn:

- **A point read of an ABSENT node is forbidden by the framework**, not merely slow: the owner
  answers an authoritative routing NotFound which **terminates the stream with an error** (it cannot
  wait for the node to appear), and that NotFound opens `MeshNodeStreamCache`'s **storm-breaker**
  window on the path — and the breaker **fast-fails WRITES too**, so the read suppresses the write
  it is waiting for. The breaker says so itself: *"A point node-access to a node that does not exist
  is a defect — read optional nodes via GetQuery (empty-on-absent), not
  GetMeshNodeStream(exactPath)."*
- **Reading that node's CONTENT out of a query is forbidden above** — unbounded lag.

**The composition, and it is the canonical pattern — listing for EXISTENCE, stream for CONTENT:**

```csharp
hub.GetQuery(id, $"path:{parent} scope:children nodeType:X select:path")   // EXISTENCE — empty-on-absent
    .Where(nodes => nodes.Any(n => string.Equals(n.Path, target, StringComparison.OrdinalIgnoreCase)))
    .Take(1)
    .SelectMany(_ => workspace.GetMeshNodeStream(target))                  // CONTENT — authoritative, live
    .Select(node => node.ContentAs<X>(hub.JsonSerializerOptions));
```

The index **trails** the store, so "the index has seen it" implies "the store has it" — the point
read opened on that signal can never be early, and never NotFounds. The same lag that disqualifies a
query for CONTENT is what makes it a safe gate. Creating the node anyway? Skip the check entirely
and use `CreateOrUpdateNodeRequest`.

🚨 **This is about ONE known path whose value you are GATING on — not about node counts.** The
worked counter-example is a token chip that reads `content` out of a
`{thread}/_Usage scope:children` query, **and it is correct** — it sums a SET to paint a total, and
a briefly-stale total is cosmetic. Converting it to N point reads would mean N per-node hub
activations per render, and on a legitimately EMPTY set (a thread with no rounds yet) every one is
an absent-node read tripping the breaker **on the render path**. So: stale answer merely looks wrong
on screen → query, `content` and all. Stale answer DECIDES whether something proceeds, passes, or is
written → the owner's stream. Full pattern:
[CqrsAndContentAccess.md](../../../src/MeshWeaver.Documentation/Data/Architecture/CqrsAndContentAccess.md)
→ "An OPTIONAL node".

**Free-floating words → vector search.** When a query contains bare text tokens
(`laptop nodeType:Story`) AND PG is the backend AND an `IEmbeddingProvider` is registered,
`PostgreSqlMeshQuery.QueryAsync` automatically routes through the HNSW cosine index instead of an
ILIKE substring scan. Structured-only queries (`nodeType:Story namespace:ACME`) stay on the regular
SQL path. Full reference:
[VectorSearch.md](../../../src/MeshWeaver.Documentation/Data/Architecture/VectorSearch.md).

## 3. Never cast an `object` payload

**`node.Content is MyType` / `payload as MyType` is a TRAP-DOOR.** It is correct only when the value
already happens to be your CLR type, and yields a **silent null** in the three cases that actually
happen in a running mesh:

1. **Untyped JSON** — the polymorphic converter DEGRADES an unresolvable `$type` to a raw
   `JsonElement` instead of throwing, so any hub whose TypeRegistry lacks the discriminator hands
   you JSON, not an instance.
2. **The as-written DOM** — application code builds content as `JsonObject`, and a change
   notification forwards that shape verbatim until the materialization pipeline re-types it.
3. **A same-named type from another assembly** — every recompile of a dynamic NodeType mints a new
   collectible assembly, so "the same" record has a different CLR identity per build.

Every one of these has caused a production outage, and they all look identical from outside: the
value reads as absent, the view renders empty, a reactive wait never completes. No exception, no
log, nothing to grep.

```csharp
// ❌ the trap-door
var store = node.Content as StoreContent;
if (delivery.Message.Payload is MySettings s) { … }

// ✅ bad-data tolerant, and it says why when it cannot convert
var store = node.ContentAs<StoreContent>(hub.JsonSerializerOptions);
var s = delivery.Message.Payload.As<MySettings>(hub.JsonSerializerOptions, logger);
```

`ContentAs<T>` is `As<T>` with the node's path in the diagnostics. Both recover a degraded
`JsonElement`/`JsonNode`, recover a SAME-short-named type from another build by JSON round-trip, and
return null for a DIFFERENTLY-named type so probe-dispatch call sites keep working.

**🚨 FIRST, though: deserialize as close as possible to where the type IS registered — which usually
means the RIGHT HUB should be handling it at all.** A `$type` resolves against the TypeRegistry
behind the options you pass, so a payload read on a hub that never registered the type is untyped by
construction, and `.As<T>()` is then papering over a routing mistake rather than bad data. The
durable fix for a repeated degradation is to move the work to the owning hub (the per-node hub for
its own content; the hub that declares the type via `WithType`) — or to register the type where the
read happens. Reach for the accessor at genuine boundaries: a cross-hub query result, a control
payload, storage JSON.

`type 'X' is not registered in this hub's TypeRegistry` → the fix is
`WithType(typeof(X), nameof(X))` on the receiving hub. See
[DebuggingMessageFlow.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DebuggingMessageFlow.md)
→ "Type-registry mismatch".

## 4. Postgres: one schema per partition

**`public.mesh_nodes` is empty by design.** Data lives in per-partition schemas (`acme.mesh_nodes`,
`rbuergi.mesh_nodes`, …).

Satellite table routing by path segment:

| Path segment | Table |
|---|---|
| `…/_Access/…` | `access` |
| `…/_Thread/…` | `threads` |
| `…/_Activity/…` | `activities` |
| `…/_Comment/…`, `_Approval`, `_Tracking` | `annotations` |
| `…/Source/…` or `…/Test/…` | `code` |
| (none) | `mesh_nodes` |

**`namespace` keeps the partition prefix — never strip it.** `namespace = rbuergi/ApiToken`, not
`ApiToken`.

**Never run raw `psql UPDATE` on a live portal** — it bypasses the workspace cache. Use
`MoveNodeRequest` or add a Repair vN migration. If you must SQL-edit, restart the portal process.

**🚨 Partition schema: provision + existence are REACTIVE + POOLED — never declare a
`PartitionDefinition` node to force a schema, never lowercase by hand.** The standard surface is on
`IPartitionStorageProvider`:

- `EnsurePartitionProvisioned(namespace) : IObservable<Unit>` — the ONE entry point that creates a
  partition's schema + tables. Reactive, idempotent (promise-cached), and **pooled** on the
  `pg:{adapter}` IoPool (the PG impl lowercases the schema correctly). Subscribe it; compose with
  `.SelectMany(_ => write…)` before writing to a not-yet-provisioned partition.
- `PartitionExists(namespace) : IObservable<bool?>` — reactive existence check (`null` =
  indeterminate; OR-fold across providers as `PartitionWriteGuardValidator` does).

The router maps a path's first segment to `seg.ToLowerInvariant()`; a `PartitionDefinition` with
`Schema` left null provisions the schema **verbatim** (`"Agent"` capital) while writes hit `"agent"`
→ 42P01. So the way to make code that writes a not-yet-provisioned partition work is
`EnsurePartitionProvisioned(p).SelectMany(_ => write…)` — **not** a partition-def node. The async
schema DDL runs inside the IoPool, never `Observable.FromAsync` (see [/async](../async/SKILL.md)).

Full reference:
[PostgresSchemaArchitecture.md](../../../src/MeshWeaver.Documentation/Data/Architecture/PostgresSchemaArchitecture.md).

## Checklist

- [ ] Every mutation is `GetMeshNodeStream(path).Update(...)` — no new `*Request`/`*Response` pair
      for a state change.
- [ ] Every cold write observable is `.Subscribe(onNext, onError)`d.
- [ ] No `Query`/`search` used to read one node's content, decide a write, or poll for completion.
- [ ] Node-may-not-exist waits use the listing-then-stream composition, not a bare point read.
- [ ] No `as`/`is` on a `Content`/`Payload` — `.ContentAs<T>()` / `.As<T>()` with the hub's
      `JsonSerializerOptions`.
- [ ] Writes to a fresh partition are gated on `EnsurePartitionProvisioned`.

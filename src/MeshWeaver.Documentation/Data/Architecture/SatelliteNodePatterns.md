---
Name: Satellite Node Patterns
Category: Architecture
Description: Patterns for parent-child node hierarchies — hub ownership, persistence, content updates, and routing for Threads, Comments, and similar structures
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="8" y="2" width="8" height="5" rx="1"/><rect x="1" y="17" width="8" height="5" rx="1"/><rect x="15" y="17" width="8" height="5" rx="1"/><path d="M12 7v4M5 17v-2a4 4 0 0 1 4-4h6a4 4 0 0 1 4 4v2"/></svg>
---

# Satellite Node Patterns

A **satellite node** is any node whose `MainNode` points to a parent node. Threads and their messages, documents and their comments, approvals, activities — all follow this shape. The pattern gives each child its own hub, its own persistence, and a well-defined ownership boundary.

This page covers the invariants that every satellite type must respect, the pitfalls that are easy to hit, and reference examples from the two canonical implementations: Thread/ThreadMessage and Comment/Reply.

> **Two satellite pages, two scopes:** this page covers the **operational invariants** — hub ownership, persistence/table routing, content-update mechanics. Its companion [Satellite Entity Patterns](/Doc/Architecture/SatelliteEntityPatterns) covers the **data model, handler, access-control, and test patterns**. Build with that one; debug ownership/persistence with this one.
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 340" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="290" y="14" width="180" height="52" rx="10" fill="#1e88e5"/>
  <text x="380" y="36" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Parent Node</text>
  <text x="380" y="53" font-family="sans-serif" font-size="11" fill="#e3f2fd" text-anchor="middle">User/alice/_Thread/chat-1</text>
  <rect x="290" y="82" width="180" height="30" rx="6" fill="#1565c0" fill-opacity=".7"/>
  <text x="380" y="102" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Hub · Workspace · Persistence</text>
  <line x1="380" y1="112" x2="160" y2="160" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="112" x2="380" y2="160" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="112" x2="600" y2="160" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="60" y="160" width="200" height="52" rx="10" fill="#43a047"/>
  <text x="160" y="181" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Satellite Node</text>
  <text x="160" y="198" font-family="sans-serif" font-size="10" fill="#e8f5e9" text-anchor="middle">…/chat-1/msg1  (ThreadMessage)</text>
  <rect x="280" y="160" width="200" height="52" rx="10" fill="#43a047"/>
  <text x="380" y="181" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Satellite Node</text>
  <text x="380" y="198" font-family="sans-serif" font-size="10" fill="#e8f5e9" text-anchor="middle">…/chat-1/msg2  (ThreadMessage)</text>
  <rect x="500" y="160" width="200" height="52" rx="10" fill="#43a047"/>
  <text x="600" y="181" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Satellite Node</text>
  <text x="600" y="198" font-family="sans-serif" font-size="10" fill="#e8f5e9" text-anchor="middle">…/chat-1/msg3  (ThreadMessage)</text>
  <rect x="60" y="224" width="200" height="28" rx="6" fill="#2e7d32" fill-opacity=".7"/>
  <text x="160" y="242" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Hub · Workspace · Persistence</text>
  <rect x="280" y="224" width="200" height="28" rx="6" fill="#2e7d32" fill-opacity=".7"/>
  <text x="380" y="242" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Hub · Workspace · Persistence</text>
  <rect x="500" y="224" width="200" height="28" rx="6" fill="#2e7d32" fill-opacity=".7"/>
  <text x="600" y="242" font-family="sans-serif" font-size="11" fill="#fff" text-anchor="middle">Hub · Workspace · Persistence</text>
  <rect x="20" y="284" width="320" height="42" rx="8" fill="none" stroke="#f57c00" stroke-width="1.5" stroke-opacity=".7"/>
  <text x="180" y="302" font-family="sans-serif" font-size="11" fill="#f57c00" fill-opacity=".9" text-anchor="middle">threads table</text>
  <text x="180" y="318" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".6" text-anchor="middle">path contains _Thread → routed here</text>
  <rect x="360" y="284" width="380" height="42" rx="8" fill="none" stroke="#8e24aa" stroke-width="1.5" stroke-opacity=".7"/>
  <text x="550" y="302" font-family="sans-serif" font-size="11" fill="#8e24aa" fill-opacity=".9" text-anchor="middle">comments table</text>
  <text x="550" y="318" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".6" text-anchor="middle">path contains _Comment → routed here</text>
  <text x="380" y="10" font-family="sans-serif" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".75" text-anchor="middle"></text>
</svg>

*Each satellite node owns its own hub, workspace, and persistence; path-segment routing maps the whole subtree to the correct PostgreSQL table.*

---

## Hub Ownership and Persistence

Every node in MeshWeaver has its own hub, created on demand when a message is routed to its address. The hub is the sole owner of the node's persistent state.

> **Rules every satellite type must follow**
> - Register `AddMeshDataSource()` in the node's `HubConfiguration`.
> - Persistence is managed by `MeshNodeTypeSource` with debounced saves — never write to storage directly.
> - No external code accesses a node's persistence via `IMeshService` or `IMeshQuery` while the hub is active.
> - The hub's workspace stream (`GetStream<MeshNode>()`) is the single source of truth for the node's content.

---

## Never Await in Hub Handlers

Hub message handlers run on the hub's serial execution block. Any `await` that waits for the same execution block to process another message will deadlock — the block is already occupied. Everything is `IObservable<T>` end-to-end: compose and `Subscribe`, never `await`.

```csharp
// WRONG — deadlocks: the await parks the action block that must process
// the very response this call is waiting for.
private static async Task<IMessageDelivery> HandleRequest(IMessageHub hub, ...)
{
    await meshService.CreateNodeAsync(node);
}

// CORRECT — return immediately; the observable chain does the work off the
// execution block and posts the response from its terminal events.
private static IMessageDelivery HandleRequest(IMessageHub hub, ...)
{
    meshService.CreateNode(node)
        .Subscribe(
            _  => hub.Post(new SomeResponse { ... }, o => o.ResponseFor(delivery)),
            ex => logger.LogError(ex, "create failed"));
    return delivery.Processed();
}
```

> 🚨 `meshService.CreateNodeAsync(...)` still exists as a **back-compat `Task` shim** over the observable
> (`MeshServiceExtensions`). It is not the pattern — a `Task` on the hub path is the deadlock. Use
> `meshService.CreateNode(node)` and `Subscribe`.

### Allowed Patterns

| Pattern | When to use |
|---|---|
| `hub.Post(message)` | Fire-and-forget to same or another hub |
| `hub.Observe<TResponse>(request, options?).Subscribe(onNext, onError)` | Request/response — the **only** request/response primitive |
| `.SelectMany(...)` / `.Select(...)` | Chain dependent work into one observable |
| `hub.InvokeAsync(action, exceptionCallback)` | Marshal an external callback back onto the hub's action block (both arguments are required) |
| `stream.Subscribe(callback)` | React to workspace stream changes |

> 🚨 **`RegisterCallback` and `AwaitResponse` do not exist.** They were **deleted** from `IMessageHub`,
> not deprecated — the interface states it plainly: *"No Task-returning request/response API on the
> interface anymore … There's no callback registration, no `TaskCompletionSource`, and no `Task`."*
> Code written against either name does not compile. `hub.Observe(request, options?)` (`AsyncSubject`-backed)
> is the whole surface; tests use `MonolithMeshTestBase.AwaitResponseAsync(...)`.

### Forbidden Patterns

| Pattern | Why |
|---|---|
| `await` in hub handlers | Deadlocks the execution block |
| `Task.Run(async () => ...)` | Breaks workspace stream propagation |
| `.GetAwaiter().GetResult()` | Blocks the execution thread |
| `.ContinueWith(t => ...)` | A `Task` continuation on the hub path — compose with `SelectMany` instead |
| `Observable.FromAsync(...)` | Runs the prologue on the subscribing (hub) thread and is unbounded — go through `IIoPool` |

---

## Updating Node Content

To update a node's content — for example, appending a message ID to a Thread's list — use the one mutation API, `GetMeshNodeStream(path).Update(...)`. It works for the hub's own node and for any other node in the mesh; the owning hub's single-threaded action block serialises every writer, and only an RFC 7396 merge patch of the fields you actually changed crosses the wire.

```csharp
// Cold — the write runs on Subscribe. Always subscribe, always with an error handler.
workspace.GetMeshNodeStream(path)
    .Update(node => node with { Content = newContent })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed for {Path}", path));
```

> **Do not reach for `DataChangeRequest` from application code**, and never read a node's current content with `GetStream<MeshNode>().Take(1)` before writing — `Take(1)` on a live stream freezes the binding, and the read-modify-write races every other writer. `stream.Update` is the read-modify-write, done on the owner. `DataChangeRequest`/`PatchDataRequest` are the plumbing `Update` itself uses.

> **Never serialize manually.** Let the framework's polymorphic converter emit the `$type` discriminators; hand-rolled `JsonSerializer.SerializeToElement(...)` of a node's content produces a payload the deserializer can reject.

---

## Thread + ThreadMessage Pattern

Threads are satellite nodes stored under `User/{userId}/_Thread/`. Each Thread owns an ordered list of ThreadMessage children:

```
User/Roland/_Thread/hello-world-4651          (Thread node)
User/Roland/_Thread/hello-world-4651/msg1     (ThreadMessage node)
User/Roland/_Thread/hello-world-4651/msg2     (ThreadMessage node)
```

### Data Flow

1. **`Thread.Messages`** stores an ordered `ImmutableList<string>` of child message IDs
   (`src/MeshWeaver.AI/Thread.cs`). Queued-but-not-yet-started input sits separately in
   `Thread.PendingUserMessages`.
2. **Submission is a node write, not a wire message.** Callers use the canonical extensions in
   `src/MeshWeaver.AI/HubThreadExtensions.cs` — `hub.StartThread(...)` / `hub.SubmitMessage(...)` —
   which write the thread node via `GetMeshNodeStream(threadPath).Update(...)`. There is no
   `SubmitMessageRequest`-shaped handler to write.
3. **The per-thread submission watcher reacts to that state change**: it drains
   `PendingUserMessages` into `Messages`, allocates the user + response cells, and invokes
   `ThreadExecution.ExecuteMessageAsync(execHub, RoundParams, AccessContext?)` **directly as a
   method** — no message dispatch. It returns `IObservable<Unit>`; the watcher subscribes and treats
   completion (gated on the terminal `Status` write) as round-done.
4. **The `_Exec` hosted hub** owns the round: its round watcher sees `Status = StartingExecution` and
   dispatches, so the streaming loop never runs on the thread node's own action block.
5. **Blazor view** data-binds a `ThreadViewModel` that wraps the messages list.

Full reference: [Thread Operations](/Doc/Architecture/ThreadOperations).

### ThreadViewModel and Data Binding

Raw arrays cannot be deserialized by `GetStream<object>`. `ThreadViewModel`
(`src/MeshWeaver.AI/ThreadViewModel.cs`) wraps the list and overrides `Equals` so a re-emission with
identical contents does not churn the UI:

```csharp
public record ThreadViewModel
{
    // ... bubble list + status state ...
    // Custom Equals compares element-wise to suppress redundant UI updates
}
```

Push via `host.UpdateData()` with `DistinctUntilChanged()`. The Blazor view binds via `JsonPointerReference` and a converter that extracts the typed object.

---

## Comment + Reply Pattern

Comments are satellite nodes stored under `{docPath}/_Comment/`. Replies are children of the Comment node:

```
Doc/MyDoc/_Comment/abc123              (Comment node)
Doc/MyDoc/_Comment/abc123/reply1       (Reply node)
```

### Key Differences from Threads

| Aspect | Thread/Message | Comment/Reply |
|---|---|---|
| Mutation entry point | The `hub.StartThread` / `hub.SubmitMessage` extensions (`HubThreadExtensions`) | Click actions in layout areas |
| Child list | Indexed `Thread.Messages` on the parent | Discovered by querying the comment's direct-child `Comment` nodes |
| Text edits | `stream.Update` on the response cell, driven from the `_Exec` hub | Direct `stream.Update` |
| Node creation | `meshService.CreateNode(...)` composed into the round's observable chain | `CreateNode` (Active) → edit via `stream.Update` |

> `Comment.Replies` still exists on the record, but the current renderer does **not** read it — it
> discovers replies with a live child query so a reply written by any writer shows up without the
> parent's list being maintained in lockstep (`CommentLayoutAreas`).

---

## PostgreSQL Table Routing

Both Thread/ThreadMessage and Comment/Reply nodes are stored in satellite tables. The default layout is `SatelliteTableMapping.Defaults`; a partition may override it through `PartitionDefinition.TableMappings`:

```json
{ "_Thread": "threads", "_ThreadMessage": "threads", "_Comment": "annotations" }
```

The routing is path-based, so children automatically inherit the parent's table:

| Path | Table |
|---|---|
| `User/alice/_Thread/chat-1` | `threads` |
| `User/alice/_Thread/chat-1/msg1` | `threads` (path contains `_Thread`) |
| `Doc/MyDoc/_Comment/abc123` | `annotations` |
| `Doc/MyDoc/_Comment/abc123/reply1` | `annotations` (path contains `_Comment`) |

> 🚨 **There is no `comments` table.** `_Comment` shares the **`annotations`** table with `_Approval`
> and the legacy `_Tracking` — see `SatelliteTableMapping.Defaults`
> (`src/MeshWeaver.Mesh.Contract/SatelliteTableMapping.cs`), which is the single source of truth for
> segment → table. Other segments in the same set: `_Activity` → `activities`, `_UserActivity` →
> `user_activities`, `_Access` → `access`, `_Notification` → `notifications`, and `Source`/`Test` →
> `code`.

---

## ConfigureDefaultNodeHub

`MeshBuilder.ConfigureDefaultNodeHub()` registers configuration that applies to **all** node hubs. Both Monolith and Orleans routing must compose this overlay with the node's own `HubConfiguration` — not replace it:

```csharp
// Correct: compose default config with the node's own config
var hubConfig = defaultConfig != null
    ? config => nodeConfig(defaultConfig(config))
    : nodeConfig;
```

Skipping this composition means the shared registrations — type-registry entries such as `config.TypeRegistry.AddAITypes()`, default layout areas, and the framework's own watchers — are absent from the node hub, so cross-hub messages arrive as raw `JsonElement` and areas silently fail to resolve.

---

## Type Registry

AI types must be registered on all three hub boundaries or cross-hub messages will arrive as raw `JsonElement` and fail to deserialize:

| Hub | Registration call |
|---|---|
| Mesh hub | `ConfigureHub(config => config.TypeRegistry.AddAITypes())` |
| Client hub | `configuration.TypeRegistry.AddAITypes()` in `AddChatViews()` |
| Node hubs | Inherited via `ConfigureDefaultNodeHub` composition (see above) |

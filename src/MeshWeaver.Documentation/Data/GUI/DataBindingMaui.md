---
Name: Data Binding in a MAUI Client
Category: Documentation
Description: How a native MAUI (or any external participant) client binds to live mesh node streams — GetMeshNodeStream to read, stream.Update to write — the same reactive, single-source-of-truth pattern the Blazor GUI uses.
Icon: /static/DocContent/GUI/DataBinding/icon.svg
---

This is the **MAUI counterpart to [Data Binding in MeshWeaver Layout](/Doc/GUI/DataBinding)**. A native client (the Memex MAUI app) binds to mesh data the *exact same way* the Blazor GUI does — there is one read path and one write path, and they are reactive and live. Only the host plumbing differs: there is no `BlazorView`/`AddBinding`, UI updates marshal to the MAUI main thread, and the hub is the **SignalR participant** hub (so the stream flows over the socket — see [SignalR Mesh Participant](/Doc/Architecture/SignalRMeshParticipant)).

---

# The Golden Rule is identical

> **🚨 Read a node with `hub.GetMeshNodeStream(path)`. Write it with `.Update(...)`. There is no other source of truth — no local replica, no `QueryAsync` for a single node, no "save" copy.**

The reasons carry over verbatim:

1. **Live updates.** The subscription stays open for the lifetime of the view/service; the owning hub pushes every change down the socket. A snapshot freezes.
2. **CQRS-correct reads.** `GetMeshNodeStream(path)` is the authoritative read — the owning hub's workspace, not the lagged index. See [CQRS](/Doc/Architecture/CqrsAndContentAccess).
3. **One coherent store.** Writes route through the same shared handle, so your own read subscription receives the echo and every other client watching the path sees it too.

## Responsibility split (MAUI)

| Side | Responsibility |
|---|---|
| **Owning hub (portal)** | Owns the node. Serialises every writer's patch on its single-threaded action block. |
| **MAUI view / service** | Hold the participant `IMessageHub`. Subscribe to `hub.GetMeshNodeStream(path)`; marshal to the UI thread; dispose on teardown. Write edits via `hub.GetMeshNodeStream(path).Update(...)`. |

---

# Read: subscribe, marshal to the UI thread

`hub.GetMeshNodeStream(path)` returns the same handle as in Blazor — it **is** `IObservable<MeshNode>` (read) and has `.Update(...)` (write). Resolve the hub from the MAUI DI (it's the participant registered with `AddMessageHubs(..., UseSignalRClient(...))`).

```csharp
public sealed class ClientConfigService : IDisposable
{
    private readonly IMessageHub _hub;
    private IDisposable? _sub;
    public MemexClientContent? Config { get; private set; }
    public event Action? Changed;

    public ClientConfigService(IMessageHub hub) => _hub = hub;

    public void Bind(string configPath)
    {
        _sub?.Dispose();
        _sub = _hub.GetMeshNodeStream(configPath)
            .Where(node => node is not null)
            .Select(node => node.ContentAs<MemexClientContent>(_hub.JsonSerializerOptions))
            .Where(c => c is not null)
            .DistinctUntilChanged()
            .Subscribe(
                content => MainThread.BeginInvokeOnMainThread(() =>   // ← UI thread hop
                {
                    Config = content;
                    Changed?.Invoke();
                }),
                ex => MainThread.BeginInvokeOnMainThread(() =>
                    /* surface: toast / status — UnauthorizedAccessException ⇒ not signed in / no Read */ { }));
    }

    public void Dispose() => _sub?.Dispose();
}
```

Differences from the Blazor view, and *only* these:

- **`MainThread.BeginInvokeOnMainThread`** wraps every UI mutation — the emission lands on a pool/socket thread, not the UI thread.
- **Dispose the subscription** in the page's `Dispose` / a service that the DI owns. (Blazor's `AddBinding` did this for you.)
- The stream is **access-checked** exactly as in Blazor: no Read ⇒ the observable terminates with `UnauthorizedAccessException`. Handle it (the participant must be **authenticated** — its token stamps identity; see [SignalR Mesh Participant](/Doc/Architecture/SignalRMeshParticipant) → Identity).
- **No `.Take(1)`** — that freezes the binding. Stay subscribed.

---

# Write: per-field read-modify-write straight to the node

The same handle is the write path. `Update` takes `MeshNode → MeshNode` and returns a cold `IObservable<MeshNode>` — **subscribe or nothing happens**. Touch only the field you're changing (the owner merges an RFC-7396 patch, so concurrent writers don't clobber each other).

```csharp
public void AddSite(string configPath, MemexClientSite site)
{
    _hub.GetMeshNodeStream(configPath)
        .Update(node =>
        {
            var c = node.ContentAs<MemexClientContent>(_hub.JsonSerializerOptions) ?? new MemexClientContent();
            return node with { Content = c with { Sites = c.Sites.Add(site) } };
        })
        .Subscribe(_ => { }, ex => /* log */ { });
}
```

Because the write routes through the same handle your read is subscribed to, the new value comes **back** to you (and to the portal, and to every other client) — no second "refresh" call, no `SubmitMessageRequest`, no save loop.

---

# 🚨 ABSOLUTE: the same forbidden antipattern, in MAUI form

Do **not** copy the node into a local store (`Preferences`, an in-memory model, a `/data` replica) and write it back on a timer/debounce/Save button. That is the [replicate-then-save antipattern](/Doc/GUI/DataBinding) — two stores drift, the save loop clobbers fields it didn't edit. The node stream is the store.

**The one sanctioned local store is the bootstrap.** A device keeps *only* what it needs to connect before it can read the mesh: the **installation id**, the **first portal URL**, and the **API token** (in `SecureStorage`). Everything else — the portal list, voice settings, display name — lives on the installation's `MemexClient` node at `{user}/Client/{installationId}` and is read/written by the rule above. That is why the config is centrally controllable from the portal: it's just a node.

```csharp
// Bootstrap (local, minimal) → connect → bind the rich config from the mesh:
var path = MemexClientNodeType.PathFor(user, installationId);   // {user}/Client/{installationId}
configService.Bind(path);                                       // GetMeshNodeStream + Update from here on
```

> **Create-on-absent**: a node you bind to must exist first. Check existence with a query (empty-on-absent) and `meshService.CreateNode(...)` once; never `GetMeshNodeStream(absentPath).Update(...)` (it NotFound-storms). See [CQRS](/Doc/Architecture/CqrsAndContentAccess).

---

# Async note

The MAUI app is **not** hub code, so ordinary `async/await` is fine for app concerns (mic capture, file IO, navigation). But **mesh reads/writes stay `IObservable` + `Subscribe`** — never `await GetMeshNodeStream(...)` or `.Result`. The reactive shape is what keeps the binding live. (Inside the MeshWeaver framework the no-async rules still apply — see [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls).)

---

# See also

- [Data Binding in MeshWeaver Layout](/Doc/GUI/DataBinding) — the Blazor original this mirrors.
- [SignalR Mesh Participant](/Doc/Architecture/SignalRMeshParticipant) — how the participant hub + identity make `GetMeshNodeStream` work over the socket.
- [Requesting Work via stream.Update()](/Doc/Architecture/RequestViaStreamUpdate) — `RequestedX` fields + watchers, the write-side state-machine pattern.
- The `/maui` skill — the step-by-step for building a MAUI client feature.

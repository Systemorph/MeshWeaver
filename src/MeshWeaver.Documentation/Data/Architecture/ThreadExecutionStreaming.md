---
NodeType: Markdown
Name: "Thread Execution & Message Rendering — Fully Data-Bound"
Abstract: "How AI thread execution writes streaming content into a thread message and how the GUI renders it. The writer pushes deltas through the shared per-path stream handle (hub.GetMeshNodeStream, backed by IMeshNodeStreamCache) via .Update. The layout area ships a path-bound control. The Blazor view subscribes to the same shared handle and renders directly — single source of truth, no per-chunk hub posts, no layout data section, no JsonPointerReference chain."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><rect x='4' y='6' width='12' height='8' rx='2' fill='white'/><rect x='8' y='12' width='12' height='8' rx='2' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "AI"
  - "Streaming"
  - "DataBinding"
---

> **Read first:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) · [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) · [Data Binding](/Doc/GUI/DataBinding) · [Per-Hub TaskScheduler](/Doc/Architecture/OrleansTaskScheduler)

## The one-sentence architecture

The writer, the layout area, and the Blazor view all touch the **same** per-message workspace stream. There is no intermediary, no republishing, no data section transform — just a single stream that the AI execution loop pushes deltas into and the view subscribes to directly.

## Quick reference

| Side | Responsibility | Primitive |
|---|---|---|
| **Writer** — AI execution on `_Exec` | Push every delta via `.Update(...)` through the thread hub's shared per-path handle. | `parentHub.GetMeshNodeStream(responsePath).Update(node => node with { Content = ... })` — backed by the process-wide `IMeshNodeStreamCache` |
| **Layout area** — backend | Return a path-bound bubble control. No data-section transform, no subscribe-and-republish. | `new ThreadMessageBubbleControl { NodePath = $"{threadPath}/{messageId}" }` |
| **Blazor view** — renderer | Subscribe in `BindData()` via `AddBinding`. Re-render on every emission. | `Hub.GetMeshNodeStream(ViewModel.NodePath)` — the **same** cache handle the writer pushes through |

## Data flow

<svg viewBox="0 0 760 220" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="20" y="70" width="160" height="80" rx="10" fill="#1565c0"/>
  <text x="100" y="104" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">_Exec Hub</text>
  <text x="100" y="122" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">(AI execution)</text>
  <text x="100" y="139" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#90caf9">responseStream.Update()</text>
  <rect x="290" y="70" width="180" height="80" rx="10" fill="#00695c"/>
  <text x="380" y="100" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Owning Per-Node Hub</text>
  <text x="380" y="118" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b2dfdb">validates · persists</text>
  <text x="380" y="135" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b2dfdb">broadcasts patches</text>
  <rect x="570" y="70" width="160" height="80" rx="10" fill="#6a1b9a"/>
  <text x="650" y="104" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Blazor View</text>
  <text x="650" y="122" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#e1bee7">GetRemoteStream</text>
  <text x="650" y="139" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ce93d8">re-renders on emit</text>
  <line x1="180" y1="110" x2="288" y2="110" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <text x="234" y="103" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".65">RFC 7396 patch</text>
  <line x1="470" y1="110" x2="568" y2="110" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <text x="519" y="103" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".65">sync broadcast</text>
  <rect x="270" y="180" width="220" height="26" rx="6" fill="none" stroke="currentColor" stroke-opacity=".25" stroke-dasharray="4,3"/>
  <text x="380" y="197" text-anchor="middle" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".55">layout area returns NodePath-bound control only</text>
</svg>

*Single source of truth: writer pushes deltas, owning hub serialises and broadcasts, Blazor view re-renders — no republishing.*

```
_Exec hub ──responseStream.Update(...)──► owning per-node hub
                                              │
                                              ├─ validates
                                              ├─ persists
                                              └─ broadcasts via synchronization protocol
                                                    │
                                                    └─ subscribers (incl. the Blazor view) re-render
```

Per-chunk pushes are one-way and fire-and-forget — the writer's action block is never blocked waiting for a return path. Only the **terminal** status write is composed into the round's completion gate (the `IObservable<Unit>` the submission watcher subscribes to). Tool-call hub round-trips are orthogonal — they run on entirely different schedulers.

## Writer side: route every push through the shared stream handle

```csharp
// ExecuteMessageAsync is a direct method call (not a hub handler). The
// submission watcher invokes it after draining PendingUserMessages via
// stream.Update, and SUBSCRIBES to the returned observable: it completes
// when the terminal Status write has landed — that completion is how the
// watcher knows the round is over.
internal static IObservable<Unit> ExecuteMessageAsync(
    IMessageHub hub, RoundParams request, AccessContext? userAccessContext)
{
    var parentHub = hub.Configuration.ParentHub!;   // the thread hub — _Exec has no AddData
    var segment   = new ActiveResponseSegment(request.ResponseMessageId);
    parentHub.Set(segment);   // check_inbox reaches this to split cells mid-round

    // Every push routes through parentHub.GetMeshNodeStream — the process-wide
    // IMeshNodeStreamCache hands out ONE shared handle per path, the same handle
    // the GUI's ThreadMessageBubbleView reads from. (A per-run
    // workspace.GetRemoteStream would open a SECOND upstream handle whose writes
    // the GUI never sees — that was a real bug, since fixed.)
    IObservable<MeshNode> PushToResponseMessage(string text, /* tool calls, status, … */)
        => parentHub.GetMeshNodeStream($"{request.ThreadPath}/{segment.ResponseMsgId}")
            .Update(node =>
            {
                // Bad-data tolerance: never clobber a node whose content can't be read.
                var current = node.ContentAs<ThreadMessage>(parentHub.JsonSerializerOptions, logger);
                if (node?.Content is not null && current is null) return node;
                current ??= new ThreadMessage { Role = "assistant", Status = ThreadMessageStatus.Streaming, … };
                return node with { Content = current with { Text = …, ToolCalls = …, Status = … } };
            });

    // Streaming chunks: Subscribe(...) fire-and-forget — the writer's action
    // block never waits. Terminal status: the returned IObservable<MeshNode> is
    // COMPOSED into the round's completion gate, so ExecuteMessageAsync only
    // completes after the Completed/Cancelled/Error write has landed.
    return clientObs.Take(1).SelectMany(chatClient => /* streaming loop */ …);
}
```

Things worth noting about this shape:

- **The round is an observable, not a fire-and-forget void.** The submission watcher subscribes to the returned `IObservable<Unit>`; completion is gated on the terminal `Status` write. Streaming-chunk pushes stay fire-and-forget (`.Subscribe(...)`) for throughput.
- **One shared handle per path.** `parentHub.GetMeshNodeStream(path)` resolves the process-wide `IMeshNodeStreamCache` — reader (GUI) and writer share the same upstream subscription, so every push is visible to every reader in order.
- **The text accumulator is closure-state, not workspace-state.** The writer owns the running text locally and ships the whole content each push — no read-modify-write against the stream.
- **The update lambda defends its fields.** Terminal `Status` can't regress to `Streaming` (a late buffered push would flicker the UI), `Text` only grows while streaming, `ToolCalls` are merged by delegation path (a concurrent terminal stamp must not be clobbered), and `UpdatedNodes` accumulate by path. Unreadable content degrades via `ContentAs<T>` instead of being overwritten.
- **Mid-round interruptions go through `ActiveResponseSegment`.** The `check_inbox` tool freezes the current cell, switches the segment to a fresh response cell, and subsequent pushes follow it — see [ThreadOperations](/Doc/Architecture/ThreadOperations).
- **Throttling is still important.** `stream.Update` is cheap, but the synchronization protocol serializes per chunk; the loop samples (~100 ms) rather than pushing per token.

## Layout-area side: ship a path-bound template

```csharp
public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
{
    var hubPath   = host.Hub.Address.ToString();
    var lastSlash = hubPath.LastIndexOf('/');
    var threadPath = lastSlash > 0 ? hubPath[..lastSlash] : hubPath;
    var messageId  = lastSlash > 0 ? hubPath[(lastSlash + 1)..] : hubPath;

    // Declare what to render and where to read its content from.
    return Observable.Return((UiControl?)new ThreadMessageBubbleControl
    {
        NodePath  = $"{threadPath}/{messageId}",
        ThreadPath = threadPath,
        MessageId  = messageId,
    });
}
```

The `Overview` method is a pure factory: given a thread-message hub path, return a control that points at it. No reactive chain, no data section, no `host.UpdateData`. The Blazor view does all the work of resolving the path to live content.

## Blazor view: hold a stream, subscribe, render

```csharp
public partial class ThreadMessageBubbleView : BlazorView<ThreadMessageBubbleControl, ThreadMessageBubbleView>
{
    private ISynchronizationStream<MeshNode>? _nodeStream;

    private string? Role        { get; set; }
    private string? AuthorName  { get; set; }
    private string? messageText { get; set; }
    private IReadOnlyList<ToolCallEntry>?   toolCalls    { get; set; }
    private IReadOnlyList<NodeChangeEntry>? updatedNodes { get; set; }

    protected override void BindData()
    {
        base.BindData();

        // Legacy fallback for callers that pass concrete Text/ToolCalls
        if (string.IsNullOrEmpty(ViewModel.NodePath))
        {
            DataBind(ViewModel.Text,         x => x.messageText, /* ... */);
            DataBind(ViewModel.ToolCalls,    x => x.toolCalls,   /* ... */);
            DataBind(ViewModel.UpdatedNodes, x => x.updatedNodes,/* ... */);
            return;
        }

        // Canonical path: subscribe to the shared per-path handle — the SAME
        // IMeshNodeStreamCache entry the writer pushes through. The patch the
        // writer ships arrives here and the view re-renders.
        var stream = Hub.GetMeshNodeStream(ViewModel.NodePath);

        AddBinding(stream
            .Where(node => node?.Content is ThreadMessage)
            .Select(node => (Node: node, Msg: (ThreadMessage)node.Content!))
            .DistinctUntilChanged(t => (t.Msg.Text, t.Msg.ToolCalls, t.Msg.UpdatedNodes))
            .Subscribe(t =>
            {
                Role         = t.Msg.Role;
                AuthorName   = t.Msg.AuthorName ?? t.Msg.AgentName ?? "Assistant";
                messageText  = t.Msg.Text;
                toolCalls    = t.Msg.ToolCalls;
                updatedNodes = t.Msg.UpdatedNodes;
                InvokeAsync(StateHasChanged);
            }));
    }
}
```

Key shape:

- **`AddBinding(...)` owns the lifetime.** The base class disposes the subscription on component teardown; the upstream cache entry stays alive for the process — no view-local handle field needed.
- **No `.Take(1)`.** The view stays subscribed for its lifetime; every chunk-tick from the writer triggers a re-render.
- **No `JsonPointerReference` indirection.** The control's `NodePath` is the only binding the layout area declares; the view does the resolve.
- **Same handle as the writer.** The writer's `parentHub.GetMeshNodeStream(path).Update(...)` and this view's `Hub.GetMeshNodeStream(path)` resolve the identical process-wide `IMeshNodeStreamCache` entry — one upstream subscription, every write visible to every reader, in order.

## Anti-patterns

| ❌ Don't | Why it's wrong | ✅ Do instead |
|---|---|---|
| `parentHub.Post(new UpdateThreadMessageContent { ... }, o => o.WithTarget(...))` per chunk | Two-hop write per chunk; the receiving hub's action block activates 30+ times during a single streaming run | `responseStream.Update(node => node with { Content = ... })` on a long-lived stream |
| `host.SubscribeToDataStream(dataKey, syncStream.Select(... => ThreadMessageViewModel.FromMessage(m)))` | Layout-area-as-republisher; content goes through three intermediaries before the view sees it | `new ThreadMessageBubbleControl { NodePath = ... }` — the view subscribes directly |
| `new ThreadMessageBubbleControl().WithText(new JsonPointerReference($"{dataPointer}/text"))` | Bind-by-value through a layout data section; freezes if the republish chain stalls | `new ThreadMessageBubbleControl { NodePath = path }` — bind-by-path |
| Re-opening `_stream` per chunk | Defeats the long-lived subscription; causes per-chunk `SubscribeRequest` churn | Open once at execution start, dispose at end |
| `await meshService.QueryAsync<MeshNode>($"path:{messagePath}").FirstOrDefaultAsync()` before appending text | Lagged catalog read + manual append + write back = lost-update race | Closure-state text accumulator + `_stream.Update` ships the whole text |

## Cross-references

- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — the actor-model rules this page applies.
- [Per-Hub TaskScheduler](/Doc/Architecture/OrleansTaskScheduler) — the threading model that keeps writer, reader, and per-node hub on independent schedulers.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — the decision matrix listing `GetMeshNodeStream(path).Update(...)` as the streaming-write primitive.
- [Data Binding](/Doc/GUI/DataBinding) — the bind-by-path / bind-by-value contract applied here to thread messages.
- `src/MeshWeaver.Blazor/Components/CollaborativeMarkdownView.razor.cs:70-146` — the canonical `_nodeStream` template; the thread-message view uses the same shape.

---
NodeType: Markdown
Name: "Thread Execution & Message Rendering — Fully Data-Bound"
Abstract: "How AI thread execution writes streaming content into a thread message and how the GUI renders it. The writer opens a long-lived GetRemoteStream and pushes deltas via .Update. The layout area ships a path-bound control. The Blazor view subscribes to the same per-message stream and renders directly — single source of truth, no per-chunk hub posts, no layout data section, no JsonPointerReference chain."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><rect x='4' y='6' width='12' height='8' rx='2' fill='white'/><rect x='8' y='12' width='12' height='8' rx='2' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "AI"
  - "Streaming"
  - "DataBinding"
---

> **Read first:** [Asynchronous Calls](AsynchronousCalls) · [CQRS — Queries vs. Content Access](CqrsAndContentAccess) · [Data Binding](xref:GUI/DataBinding) · [Per-Hub TaskScheduler](OrleansTaskScheduler)

## The one-sentence architecture

The writer, the layout area, and the Blazor view all touch the **same** per-message workspace stream. There is no intermediary, no republishing, no data section transform — just a single stream that the AI execution loop pushes deltas into and the view subscribes to directly.

## Quick reference

| Side | Responsibility | Primitive |
|---|---|---|
| **Writer** — AI execution on `_Exec` | Open a long-lived remote stream at start. Push every delta via `.Update(...)`. Dispose at end. | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())` + `.Update(node => node with { Content = ... })` |
| **Layout area** — backend | Return a path-bound bubble control. No data-section transform, no subscribe-and-republish. | `new ThreadMessageBubbleControl { NodePath = $"{threadPath}/{messageId}" }` |
| **Blazor view** — renderer | Hold a `_nodeStream` field. Subscribe in `BindData()`. Re-render on every emission. | Same `GetRemoteStream<MeshNode, MeshNodeReference>` — symmetric with the writer |

## Data flow

```
_Exec hub ──responseStream.Update(...)──► owning per-node hub
                                              │
                                              ├─ validates
                                              ├─ persists
                                              └─ broadcasts via synchronization protocol
                                                    │
                                                    └─ subscribers (incl. the Blazor view) re-render
```

The flow is one-way and fire-and-forget from the writer's perspective. The writer's action block is never blocked waiting for a return path. Tool-call hub round-trips are orthogonal — they run on entirely different schedulers.

## Writer side: open the stream, push deltas, dispose

```csharp
// SubmitMessageRequest was deleted 2026-05-25 — ExecuteMessageAsync is now a
// direct method call (not a hub handler). The submission watcher subscriber
// invokes it after writing PendingUserMessages via stream.Update.
internal static void ExecuteMessageAsync(
    IMessageHub hub, RoundParams request, AccessContext? userAccessContext)
{
    var parentHub  = hub.Configuration.ParentHub!;
    var workspace  = parentHub.GetWorkspace();
    var responsePath = $"{request.ThreadPath}/{request.ResponseMessageId}";

    // Open the per-message remote stream once at start. Hold for the whole run.
    // The owning per-node hub activates on subscribe (it has the MeshNodeReference
    // reducer because ThreadMessageNodeType registers WithContentType<ThreadMessage>()).
    var responseStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(responsePath), new MeshNodeReference());

    // Closure-state accumulator. The writer owns the running text + tool-call log
    // locally and ships the whole content on every push — no read-modify-write.
    var responseText = new StringBuilder();
    var toolCallLog  = ImmutableList<ToolCallEntry>.Empty;

    void PushUpdate()
    {
        responseStream.Update(node =>
        {
            if (node?.Value?.Content is not ThreadMessage current) return node;
            return node with
            {
                Value = node.Value with
                {
                    Content = current with
                    {
                        Text      = responseText.ToString(),
                        ToolCalls = toolCallLog,
                        // ... other fields the streaming loop accumulates
                    }
                }
            };
        });
    }

    // Drive the streaming loop. Every throttle tick / tool-result / completion
    // calls PushUpdate(); no parentHub.Post per chunk.

    // Dispose at end (success / cancel / error — all paths).
    hub.RegisterForDisposal(_ => (responseStream as IDisposable)?.Dispose());

    return delivery.Processed();
}
```

Four things worth noting about this shape:

- **The text accumulator is closure-state, not workspace-state.** The writer never reads the message's current `Text` from the stream and appends to it — it owns the string locally and ships the whole thing each time. Re-pushes of identical content become no-op patches.
- **`responseStream.Update(...)` is fire-and-forget.** No `await`, no callback. The owning hub validates and broadcasts; the writer continues immediately.
- **The stream is opened once and lives for the entire run.** Subscribe once, use for every chunk, dispose at the end — not per-chunk.
- **Throttling is still important.** `stream.Update` is cheap, but the synchronization protocol serializes per chunk. The loop therefore pushes every ~3 s (or on tool-call events) to avoid hammering subscribers.

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

        // Canonical path: subscribe to the per-message remote stream — same primitive
        // the writer uses. The patch the writer ships arrives here and the view re-renders.
        _nodeStream = Hub.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(ViewModel.NodePath), new MeshNodeReference());

        AddBinding(_nodeStream
            .Where(c => c.Value?.Content is ThreadMessage)
            .Select(c => (Node: c.Value!, Msg: (ThreadMessage)c.Value!.Content!))
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

- `_nodeStream` is a **field**, not a local. `AddBinding(...)` registers the subscription with the base class so it is automatically disposed on component teardown.
- **No `.Take(1)`.** The view stays subscribed for its lifetime; every chunk-tick from the writer triggers a re-render.
- **No `JsonPointerReference` indirection.** The control's `NodePath` is the only binding the layout area declares; the view does the resolve.
- **Symmetric with the writer.** The writer calls `.Update(...)` on the stream; the owning hub broadcasts; this view — which holds an identical `GetRemoteStream` handle — receives the emission and re-renders.

## Anti-patterns

| ❌ Don't | Why it's wrong | ✅ Do instead |
|---|---|---|
| `parentHub.Post(new UpdateThreadMessageContent { ... }, o => o.WithTarget(...))` per chunk | Two-hop write per chunk; the receiving hub's action block activates 30+ times during a single streaming run | `responseStream.Update(node => node with { Content = ... })` on a long-lived stream |
| `host.SubscribeToDataStream(dataKey, syncStream.Select(... => ThreadMessageViewModel.FromMessage(m)))` | Layout-area-as-republisher; content goes through three intermediaries before the view sees it | `new ThreadMessageBubbleControl { NodePath = ... }` — the view subscribes directly |
| `new ThreadMessageBubbleControl().WithText(new JsonPointerReference($"{dataPointer}/text"))` | Bind-by-value through a layout data section; freezes if the republish chain stalls | `new ThreadMessageBubbleControl { NodePath = path }` — bind-by-path |
| Re-opening `_stream` per chunk | Defeats the long-lived subscription; causes per-chunk `SubscribeRequest` churn | Open once at execution start, dispose at end |
| `await meshService.QueryAsync<MeshNode>($"path:{messagePath}").FirstOrDefaultAsync()` before appending text | Lagged catalog read + manual append + write back = lost-update race | Closure-state text accumulator + `_stream.Update` ships the whole text |

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — the actor-model rules this page applies.
- [Per-Hub TaskScheduler](OrleansTaskScheduler) — the threading model that keeps writer, reader, and per-node hub on independent schedulers.
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — the decision matrix listing `GetRemoteStream(...).Update(...)` as the streaming-write primitive.
- [Data Binding](xref:GUI/DataBinding) — the bind-by-path / bind-by-value contract applied here to thread messages.
- `src/MeshWeaver.Blazor/Components/CollaborativeMarkdownView.razor.cs:70-146` — the canonical `_nodeStream` template; the thread-message view uses the same shape.

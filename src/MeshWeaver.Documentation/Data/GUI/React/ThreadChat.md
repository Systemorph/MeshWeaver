---
Name: Thread Chat in React
Category: Documentation
Description: The React chat — watching the thread node and its message satellites, composer gating, and submission via startThread/submitMessage: the same node shapes as the .NET HubThreadExtensions surface, no parallel protocol.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>
---

# Thread Chat in React

`ThreadChatView` (`clients/react/src/controls/threadChat.tsx`) is the React port of the Blazor portal's `ThreadChatView.razor` — the full chat experience over a **thread node's live stream**. There is no chat protocol of its own: the view watches and writes the exact node shapes the .NET `HubThreadExtensions` surface defines (see [Thread Operations](/Doc/Architecture/ThreadOperations)), so both frontends, the SDKs, and the agents all converge on the same thread node.

## `MeshOps` — what the chat needs beyond the area contract

Most controls only need the layout-area [`AreaSource`](../Rendering). Chat needs two more capabilities — watching arbitrary **node** streams and the canonical thread-submission operations — expressed as the `MeshOps` interface (`clients/react/src/live/meshOps.tsx`):

```ts
export interface MeshOps {
  /** Subscribe to a node's live state — yields on every change (initial state, then updates). */
  watch(path: string): AsyncIterable<MeshNodeState>;
  /** ONE CreateNodeRequest carrying the seeded thread node — the client twin of hub.StartThread. */
  startThread(namespacePath: string, userText: string, opts?: ThreadSubmitOptions):
    Promise<{ path: string; userMessageId?: string }>;
  /** JSON-merge patch queueing a message on an existing thread — the twin of hub.SubmitMessage. */
  submitMessage(threadPath: string, userText: string, opts?: ThreadSubmitOptions): Promise<string | null>;
  /** Field-level partial node update (RFC 7396) — control-plane flips like requestedStatus. */
  patch(path: string, fields: Record<string, unknown>): void;
  /** Optional mesh query — feeds the agent/model selectors (nodeType:Agent / nodeType:Model). */
  search?(query: string, basePath?: string, limit?: number): Promise<Record<string, unknown>[]>;
}
```

The renderer stays transport-free: `@meshweaver/client-web`'s `Mesh` satisfies `MeshOps` **structurally**, so the host app wires it in one line:

```tsx
import { Mesh } from "@meshweaver/client-web";
import { MeshOpsProvider } from "@meshweaver/react";

const mesh = Mesh.from(connection);          // or: await Mesh.connect(url, { token })
// <MeshOpsProvider ops={mesh}> … <RenderArea/> … </MeshOpsProvider>
// (MeshAreaView takes the same thing as its `ops` prop.)
```

Without a provider the chat renders a hint ("Thread chat needs a live mesh connection") instead of crashing.

## Data flow — mirrors Blazor exactly

1. **The thread node is the state.** The view watches `MeshOps.watch(threadPath)` and renders the node's `Thread` content: `messages` (ordered ids), `pendingUserMessages` (queued payloads keyed by id), `status` (`Idle | StartingExecution | Executing | Cancelled | Done`), `executionStatus`, `streamingText`, `streamingToolCalls`, and `composer` (the sticky agent/model selection).
2. **Each message is a satellite cell** at `{threadPath}/{id}` — one watch per id, the twin of Blazor's message subscriptions. A cell's content is a `ThreadMessage` (`role` / `text` / `status` / `toolCalls` / …). Until the cell exists, the bubble renders from the pending payload in `pendingUserMessages`, shown as *queued* — so a just-sent message appears instantly.
3. **While the thread executes**, an execution bar shows the live `executionStatus`, a `streamingText` preview, the running `streamingToolCalls`, and a **Stop** button.

## Submission — the canonical surface, not a wire protocol

Sends go through the client twin of `HubThreadExtensions` (implemented in `clients/grpc-web/src/mesh.ts` + `threads.ts`):

- **No thread yet** → `startThread(namespacePath, text, opts)`: **one `CreateNodeRequest`**, targeted at the namespace hub, carrying the thread node at `{namespace}/_Thread/{speakingId}` pre-seeded with the first user message in `content.pendingUserMessages` and the composer selection. The per-thread submission watcher on the hub dispatches the first round as soon as the thread hub activates. Once created, the view pins the returned path — message 2+ never re-creates.
- **Existing thread** → `submitMessage(threadPath, text, opts)`: an **RFC 7396 merge-patch** on the thread node appending the id to `content.userMessageIds` and the payload to `content.pendingUserMessages` — the client-side analog of `workspace.GetMeshNodeStream(threadPath).Update(...)`; the owning hub serialises the patch. Whitespace-only text resolves to `null` and nothing is enqueued.
- **Stop** → `patch(threadPath, { content: { requestedStatus: "Cancelled" } })` — the standard control-plane flip the owning hub's watcher reacts to (see [Activity Control Plane](/Doc/Architecture/ActivityControlPlane)).

Guards match the server's: a top-level/ownerless `_Thread/{id}` path is refused client-side (`isOwnerlessThreadPath`) — such a path has no per-node hub to route to. Identity is **never** claimed client-side; the server stamps the submitter from the bearer token.

Submission failures are surfaced, not swallowed — the error renders above the composer and the typed text is restored, mirroring Blazor's `onError` (a silent reset is the "message vanished, no idea why" symptom).

## Composer gating

The composer is gated exactly like Blazor's:

```ts
const isExecuting = thread.status === "StartingExecution" || thread.status === "Executing";
const canSend = !!ops && text.trim().length > 0 && !isExecuting && (!!threadPath || !!namespacePath);
```

— disabled while the text is whitespace-only or the thread is executing. Enter sends, Shift+Enter inserts a newline.

The **agent / model dropdowns** populate from the mesh when the ops expose `search` (`nodeType:Agent` / `nodeType:Model`); the selection defaults to the thread's embedded `composer` (the single source of the round's selection), and an explicit pick folds back into the composer on submit — so the choice sticks for the next round, in either frontend.

## Rendering it

`ThreadChat` is a registered control `$type` like any other: a backend layout area that emits a `ThreadChatControl` renders as this view in React (and as `ThreadChatView.razor` in Blazor). The control's bound properties — `threadPath` (or a `threadViewModel` carrying it), `initialContext` (the namespace for new threads), `hideEmptyState`, `showFullHeader` — resolve through the standard `useResolve` binding hook.

## Related

- [Thread Operations](/Doc/Architecture/ThreadOperations) — the canonical .NET submission surface these ops mirror.
- [Rendering Architecture](../Rendering) — `MeshOpsProvider` in the `MeshAreaView` composition.
- [Testing & Parity](../Testing) — the chat's vitest suite.

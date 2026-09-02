---
Name: Markdown Fence Extensions
Category: Architecture
Description: How an interactive markdown fence is built — the platform parses the fence and emits a marker, the clients hydrate it — why adding a new one is always a two-repo change, and what a client that does not know the marker must still show.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="8 6 3 12 8 18"/><polyline points="16 6 21 12 16 18"/><line x1="13" y1="4" x2="11" y2="20"/></svg>
---

# Markdown Fence Extensions

A MeshWeaver document is markdown, and some of its fenced blocks are *alive*: a ```` ```csharp --render ```` fence
runs and shows the control it produced, a ```` ```layout ```` fence embeds a live layout area, a
```` ```mermaid ```` fence draws a diagram. This page is about the seam that makes that possible, and about
the one thing everybody gets wrong on first contact with it: **a fence is never rendered by the code
that parses it.**

## The seam, in one sentence

The platform **parses** a fence and emits an inert HTML **marker**; a **client** replaces that marker
with something interactive.

<svg viewBox="0 0 780 210" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:780px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="fx-arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="8" y="14" width="330" height="182" rx="10" fill="none" stroke="#90a4ae" stroke-dasharray="4,4"/>
  <text x="24" y="34" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity="0.75">MeshWeaver (platform)</text>
  <rect x="440" y="14" width="332" height="182" rx="10" fill="none" stroke="#90a4ae" stroke-dasharray="4,4"/>
  <text x="456" y="34" font-family="sans-serif" font-size="11" font-weight="bold" fill="currentColor" fill-opacity="0.75">MeshWeaver.Plugins (clients)</text>
  <rect x="26" y="52" width="140" height="52" rx="8" fill="#5c6bc0"/>
  <text x="96" y="74" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Parser</text>
  <text x="96" y="92" font-family="sans-serif" font-size="10" fill="#c5cae9" text-anchor="middle">Markdig block parser</text>
  <rect x="190" y="52" width="130" height="52" rx="8" fill="#5c6bc0"/>
  <text x="255" y="74" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Renderer</text>
  <text x="255" y="92" font-family="sans-serif" font-size="10" fill="#c5cae9" text-anchor="middle">emits the marker</text>
  <rect x="26" y="130" width="294" height="50" rx="8" fill="#37474f"/>
  <text x="173" y="152" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">inert HTML</text>
  <text x="173" y="168" font-family="sans-serif" font-size="10" fill="#b0bec5" text-anchor="middle">&lt;div class='layout-area' data-address=… &gt;</text>
  <rect x="458" y="44" width="140" height="44" rx="8" fill="#1e88e5"/>
  <text x="528" y="71" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">Blazor</text>
  <rect x="458" y="96" width="140" height="44" rx="8" fill="#1e88e5"/>
  <text x="528" y="123" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">React</text>
  <rect x="458" y="148" width="140" height="44" rx="8" fill="#1e88e5"/>
  <text x="528" y="175" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff" text-anchor="middle">React Native</text>
  <rect x="626" y="96" width="132" height="44" rx="8" fill="#43a047"/>
  <text x="692" y="116" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">live control</text>
  <text x="692" y="131" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle">or plain HTML</text>
  <line x1="166" y1="78" x2="188" y2="78" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#fx-arr)"/>
  <line x1="255" y1="104" x2="200" y2="128" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#fx-arr)"/>
  <line x1="320" y1="155" x2="456" y2="118" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#fx-arr)"/>
  <line x1="598" y1="118" x2="624" y2="118" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#fx-arr)"/>
</svg>

*The platform never renders an interactive fence. It emits a marker, and whichever client is on the
other side decides what that marker becomes.*

## What lives where

| Piece | Home |
|---|---|
| Fence parsing + the marker's HTML | `src/MeshWeaver.Markdown` (this repository) |
| The controls a marker can resolve to | `src/MeshWeaver.Layout` (this repository) |
| Blazor hydration | `MeshWeaver.Blazor/Components/MarkdownHtmlRenderer.cs` (**`MeshWeaver.Plugins`**) |
| React hydration | `clients/react/src/controls/interactiveMarkdown.ts` (**`MeshWeaver.Plugins`**) |
| React Native hydration | the RN cell renderers (**`MeshWeaver.Plugins`**) |

**Every renderer is in the other repository.** That is the fact that decides the shape of any fence
work: a new interactive fence is a two-repo change set, platform first, and the platform half is
inert on its own by construction.

## The existing markers

`ExecutableCodeBlockRenderer` and `LayoutAreaMarkdownRenderer` between them define the whole
vocabulary. There are only two shapes, and a new fence should reuse one rather than mint a third:

| Marker | Emitted for | What a client makes of it |
|---|---|---|
| `<div class='layout-area' data-address=… data-area=… data-id=…>` | ```` ```layout ```` fences, and the result pane of every `--render` block | A live layout area — the general-purpose escape hatch: **anything expressible as a `UiControl` reaches every client through it** |
| `md-code-cell` / `code-content` / `md-code-cell-toolbar` (+ `data-submission-id`, `data-language`) | an executable block that shows its code | The notebook cell: editor, output pane, Run bar on the bottom edge |

The layout-area marker is the powerful one. A fence that can be expressed as *"render this
`UiControl` here"* needs **no client change at all** — the clients already hydrate that div, and the
control travels through the normal layout-area machinery. It comes in two forms, from the same
builder (`LayoutAreaMarkdownRenderer.GetLayoutAreaDiv` / `GetLayoutAreaDivOpenTag`): **empty**, and
**wrapping fallback content** for a client that cannot hydrate it — the ```` ```prompt ```` fence uses
the second, and the degradation rule below is why.

## 🚨 The degradation rule

`ExecutableCodeBlockRenderer` states the contract for the cell markers outright: a client that does
not hydrate them "sees an ordinary div and keeps rendering the fence read-only — the attributes are
additive." Follow that rule for anything new.

**A fence must never render as less than it did before the extension existed.** The failure to avoid
is a fence whose authored text disappears into a marker that one client turns into a rich widget and
the others turn into nothing — the document is then *worse* on those clients than the plain fenced
block it replaced, and nothing in CI can see it, because the platform's own tests only ever look at
the marker.

Concretely: emit the authored content as ordinary markup **as well as** the marker, or wrap the
marker so an un-hydrated client still shows the text. The ```` ```prompt ```` fence below is the
worked example of the second — the marker carries the read-only fenced block as its children, which a
hydrating client drops on its way to mounting the live area.

## Worked example: the `prompt` fence (#2511)

Course pages author suggested AI prompts as ```` ```prompt ```` fences. They used to render as static
fenced code: readable, but not editable and not runnable. The request was that such a fence become a
**composer pre-filled with the authored text**, whose Submit starts a real agent thread and opens it
**full page**.

Walking it through the seam gives the whole change set — and shows why the interesting half is not in
this repository.

### The platform half (this repository)

1. **Parse.** `ExecutableCodeBlock.Initialize` derives `PromptDraft` from the fence body whenever the
   info string is `prompt`, next to the `layout` block it already parses. A prompt fence never
   produces a `SubmitCodeRequest` — it is prose for an agent, not source for the kernel, and saying so
   in `GetSubmitCodeRequest` means a stray `--render` on one cannot turn it into a code cell.
2. **Emit.** `ExecutableCodeBlockRenderer.WritePromptComposer` lowers it to the **layout-area marker**
   rather than a new one, pointing at the `Prompt` area on the page node's own hub. Nothing new has to
   be taught to any client for the composer to appear.
3. **Carry the draft.** The authored text rides as the area's **reference id**, base64url-encoded
   (`PromptFence.EncodeDraft`). Not raw: an area id is concatenated into hrefs, and everything after a
   `?` in one is parsed as reference *parameters* — and a prompt is prose, full of `/`, `?`, `&` and
   newlines. This is the same encoding, for the same reason, as
   `LayoutAreaReference.GetMeshNodeDataContext`.
4. **The control.** `MeshNodeLayoutAreas.PromptComposer` returns the composer that already exists —
   `ThreadChatControl`, the same control the side panel and the Threads app mount — with the decoded
   draft on its new `InitialDraft` property and `HideEmptyState` on. That flag is load-bearing:
   `ThreadChatView` reads it as `isCompact` and navigates to the created thread **full page** instead
   of handing it to the side panel. "Submit starts a full-page thread" *is* that flag.
5. **Degrade.** The marker **wraps** the ordinary read-only fenced block. A client that hydrates
   layout areas replaces the div and drops its children; one that does not renders them — the authored
   prompt, exactly as it read before. With no owning node there is no hub to serve the area, so the
   fence stays a plain block rather than emitting an ownerless address.

### The client half (`MeshWeaver.Plugins`)

`ThreadChatView` must seed its composer from `ThreadChatControl.InitialDraft` — one-shot, into a NEW
chat only, so a draft can never clobber text the user is already typing. The machinery is there
already: `SeedPendingDraftIfAny` does exactly this job from a different source (the side panel's
one-shot `PendingComposerDraft`, the "new thread from this cell" hand-off), and the declarative draft
is the second source feeding it.

Starting the thread needs nothing new: `Hub.StartThread` and the full-page navigation are what
`ThreadChatView`'s submit already does in compact mode.

The React client hydrates layout-area markers with a regex that matches an **empty** div
(`interactiveMarkdown.ts`), so a wrapped marker falls through to the fallback there and shows the
prompt read-only — correct by the degradation rule, and a one-line widening away from the composer
when that client wants it.

### Verification

The platform half is testable here (`PromptFenceComposerTest` in `MeshWeaver.Graph.Test`): the fence
produces the expected marker, the draft round-trips through the area id, the marker wraps the
read-only fence, and the layout area produces a compact `ThreadChatControl` carrying the authored
text. The half that a learner can actually see is not — there is no Blazor in this repository — so
the acceptance check is a **rendered page**: open a course lesson that ships a ```` ```prompt ````
fence, confirm the composer shows the authored text, edit it, submit, and land on the full-page
thread. A green platform build says nothing about that.

## Related

- [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown) — the author-facing fence dialect.
- [Authoring Documentation](/Doc/Architecture/AuthoringDocumentation) — fence rules for doc pages specifically.
- [User Interface](/Doc/Architecture/UserInterface) — how a `UiControl` reaches a client at all.
- [Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection) — why the platform never reaches into a plugin repo.
- [Thread Operations](/Doc/Architecture/ThreadOperations) — what starting a thread actually does.

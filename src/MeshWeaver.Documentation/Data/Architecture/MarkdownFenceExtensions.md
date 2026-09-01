---
Name: Markdown Fence Extensions
Category: Architecture
Description: How an interactive markdown fence is built — the platform parses the fence and emits a marker, the clients hydrate it — why adding a new one is always a two-repo change, and what a client that does not know the marker must still show.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="8 6 3 12 8 18"/><polyline points="16 6 21 12 16 18"/><line x1="13" y1="4" x2="11" y2="20"/></svg>
---

# Markdown Fence Extensions

A MeshWeaver document is markdown, and some of its fenced blocks are *alive*: a ` ```csharp --render`
fence runs and shows the control it produced, a ` ```layout` fence embeds a live layout area, a
` ```mermaid` fence draws a diagram. This page is about the seam that makes that possible, and about
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
| `<div class='layout-area' data-address=… data-area=… data-id=…>` | ` ```layout` fences, and the result pane of every `--render` block | A live layout area — the general-purpose escape hatch: **anything expressible as a `UiControl` reaches every client through it** |
| `md-code-cell` / `code-content` / `md-code-cell-toolbar` (+ `data-submission-id`, `data-language`) | an executable block that shows its code | The notebook cell: editor, output pane, Run bar on the bottom edge |

The layout-area marker is the powerful one. A fence that can be expressed as *"render this
`UiControl` here"* needs **no client change at all** — the clients already hydrate that div, and the
control travels through the normal layout-area machinery.

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
marker so an un-hydrated client still shows the text.

## Worked example: the `prompt` fence (#2511)

Course pages author suggested AI prompts as ` ```prompt` fences. Today no extension claims the
`prompt` language, so they render as static fenced code: readable, but not editable and not
runnable. The request is that such a fence become a **composer pre-filled with the authored text**,
whose Submit starts a real agent thread and opens it **full-page**.

Walking it through the seam gives the whole change set, and shows why the interesting part is not in
this repository:

1. **Platform — parse.** A fence extension for the `prompt` info string, patterned on
   `ExecutableCodeBlockExtension`, carrying the fence body as the authored prompt.
2. **Platform — emit.** Lower it to the **layout-area marker** rather than a new one, pointing at a
   layout area that returns the composer. Nothing new has to be taught to any client to get the
   composer on screen.
3. **Platform — the control.** The composer already exists: `ThreadChatControl`
   (`src/MeshWeaver.Layout/ThreadChatControl.cs`), the same control the side panel and the Threads
   app mount. In compact mode (`WithHideEmptyState`) its submit already opens the new thread
   **full-page** rather than in the side panel — precisely the behaviour asked for.
4. **Clients — the two gaps.** Both are in `MeshWeaver.Plugins`, and neither has a platform-side
   substitute:
   - **Pre-filling.** `ThreadChatControl` carries no initial draft, and the only prefill path that
     exists — `SidePanelStateService.OpenNewThreadWithDraft` / `ConsumePendingComposerDraft`, used by
     "new thread from this cell" — is a client-side service that opens the **side panel**, not a
     full page. A declarative initial draft on the control, honoured by `ThreadChatView`, is the
     missing seam.
   - **Starting the thread.** `hub.StartThread` and the `Thread` node type ship with the AI engine,
     which lives in `MeshWeaver.Plugins`. The platform can *query* threads
     (`nodeType:Thread` searches in `UserActivityLayoutAreas`) and can *reference* the composer
     control, but it has no thread-creation API and must not grow one — see
     [Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection).

So: the platform half is the fence extension plus a declarative initial-draft property on the
control; the client half — the half a reader can actually see — is `ThreadChatView` honouring it.
Landing only the platform half yields an empty composer where the authored prompt used to be, which
is exactly the degradation the rule above forbids. **Land them together, platform first.**

### Verification, when it is built

The platform half is testable in this repository (the fence produces the expected marker; the layout
area produces a `ThreadChatControl` carrying the authored text). The half that matters is not —
there is no Blazor in this repository — so the acceptance check is a **rendered page**, not a
platform unit test: open a course lesson that ships a ` ```prompt` fence, confirm the composer shows
the authored text, edit it, submit, and land on the full-page thread. A green platform build says
nothing about any of that.

## Related

- [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown) — the author-facing fence dialect.
- [Authoring Documentation](/Doc/Architecture/AuthoringDocumentation) — fence rules for doc pages specifically.
- [User Interface](/Doc/Architecture/UserInterface) — how a `UiControl` reaches a client at all.
- [Repository Dependency Direction](/Doc/Architecture/RepositoryDependencyDirection) — why the platform never reaches into a plugin repo.
- [Thread Operations](/Doc/Architecture/ThreadOperations) — what starting a thread actually does.

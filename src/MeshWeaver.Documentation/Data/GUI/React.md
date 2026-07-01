---
Name: React Frontend
Category: Documentation
Description: The client-side React frontend — @meshweaver/react renders the same UiControl JSON contract as Blazor, streamed over gRPC-web with no server circuit. Architecture, parity state, and the topic map.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="2.2" fill="currentColor" stroke="none"/><ellipse cx="12" cy="12" rx="10" ry="4.2"/><ellipse cx="12" cy="12" rx="10" ry="4.2" transform="rotate(60 12 12)"/><ellipse cx="12" cy="12" rx="10" ry="4.2" transform="rotate(120 12 12)"/></svg>
---

# React Frontend

MeshWeaver has two web frontends over **one UI contract**. The classic portal is Blazor Server: layout areas render server-side and DOM diffs stream to the browser over a SignalR circuit. The React frontend renders the **same layout areas client-side**: the browser receives the raw `UiControl` JSON tree (an `{areas, data}` snapshot plus patches) over the gRPC-web mesh transport and a React + Fluent UI renderer walks it. Same hubs, same layout areas, same data binding — a different last mile.

> **The code on these pages does not execute in the portal.** The live `--render` blocks used elsewhere in the docs run *C#* in the portal's kernel; React/TSX cannot execute there. All TSX snippets in this section are plain fenced code, verified against the sources in `clients/react`, `clients/grpc-web`, and `clients/portal`.

## The three packages

| Package | Location | Role |
|---|---|---|
| `@meshweaver/react` | `clients/react` | The renderer: `$type` → Fluent UI React v9 component registry, skin dispatch, binding hooks, theming, `ThreadChat`. `@meshweaver/react/core` is the Fluent-free core for React Native / custom leaf packs. |
| `@meshweaver/client-web` | `clients/grpc-web` | The browser/React-Native mesh transport over gRPC-web (Connect-ES) — `observe` / `post` / `watch` plus the `Mesh` operations surface (`search`, `patch`, `startThread`, `submitMessage`, …). |
| portal app shell | `clients/portal` | The SPA served at `/app` on a portal deployment: header + nav chrome around the renderer — the web analog of the Blazor portal shell. |

## Architecture vs. Blazor Server

| | Blazor Server (classic) | React frontend |
|---|---|---|
| Rendering | Server-side; DOM diffs over the SignalR circuit | Client-side; the browser receives the `UiControl` JSON tree and renders it locally |
| Transport | SignalR circuit (stateful, per-tab) | gRPC-web `Connect` (server-stream) + `Deliver` (unary) — see [Rendering Architecture](Rendering) |
| Connection loss | The circuit **is** the UI state — a drop degrades to the reconnect overlay / a page reload | No server circuit. The UI state lives in the browser; the layout-area stream is a `Full` snapshot + patches, so re-opening the subscription re-syncs the tree instead of killing the UI |
| Interactivity | Events round-trip to the server component | Events post back as mesh messages (`ClickedEvent`, `PatchDataChangeRequest`, …); edits apply optimistically and the server echoes the authoritative patch |
| Extensibility | Blazor views registered per control type | A spreadable `$type` → component registry; new controls load at runtime via native ESM `import(url)` — see [Custom React Controls](../ReactCustomControls) |

Both frontends bind data the same way conceptually: the backend layout area declares *what* to render, and every value read/write rides the area's data stream (see [Data Binding](../DataBinding) for the contract). The React renderer resolves the same `/data` JSON-pointer bindings with its `useResolve` hook.

## Parity state

Parity with the Blazor portal is **pinned by a test**, not by intention. `clients/react/src/render/parity.test.ts` lists the authoritative Blazor vocabulary — every `*Control` / `*Skin` type in `src/MeshWeaver.Layout` — and fails when the React pack misses one:

- **51 / 51 leaf-control `$type`s registered** (Label, Markdown, DataGrid, Chart, ThreadChat, MeshSearch, CodeEditor, …), plus all 18 container/item skins (LayoutStack, LayoutGrid, Tabs, Card, Splitter, Toolbar, NavMenu, EditForm, …).
- **5 of the 51 are registered placeholders** — controls whose real rendering needs a live mesh service beyond the area contract: `DocumentSource`, `ExportDocument`, `FileBrowser`, `NodeExport`, `NodeImport` (the `placeholderControlTypes` list in `clients/react/src/controls/mesh.tsx`). They render a clearly-labeled placeholder card, never a crash.
- **The placeholder list is a ratchet**: the parity test pins it exactly, so implementing one for real means removing it from the list, and adding a *new* placeholder fails the build. The long-tail only ever shrinks.

Everything else — containers, forms, grids, charts, markdown, nav, dialogs, editors, the chat — renders for real. See [Testing & Parity](Testing) for the full test story.

## Topic map

| Page | What it covers |
|---|---|
| [Getting Started](GettingStarted) | The served SPA at `/app`, the `Portal:Frontend` / `Portal:ReactAppUrl` configuration, the `/frontend/{react\|blazor\|clear}` toggle + `mw-frontend` cookie, and local dev with Vite |
| [Rendering Architecture](Rendering) | The `UiControl` JSON contract, the `$type` → component registry, `MeshAreaView` + `AreaSource`, and how live areas hydrate over gRPC-web |
| [Theming](Theming) | Light/dark/system with the same localStorage contract as Blazor — one preference across both frontends |
| [Thread Chat](ThreadChat) | The React chat: thread-node watching, message satellites, composer gating, `startThread` / `submitMessage` |
| [Custom React Controls](../ReactCustomControls) | Extending the renderer with your own control — server-side `UiControl` subclass + a React component for its `$type`, runtime ESM loading |
| [Testing & Parity](Testing) | The parity ratchet, the vitest suites, and the transport round-trip test |

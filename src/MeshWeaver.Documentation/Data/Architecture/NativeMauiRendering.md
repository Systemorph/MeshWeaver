---
Description: "How the native MAUI client renders mesh layout areas as a recursive tree of native views via a model→view registry (no BlazorWebView), how markdown + the @@ operator inject views recursively, and the one keystone gap — nested/remote sub-areas not resolving across the sync boundary."
title: Native MAUI Rendering
order: 36
---

# Native MAUI Rendering

The native client (`memex/Memex.Client`) renders the MeshWeaver portal with **native MAUI views**, not a `BlazorWebView`. This is deliberate: the Blazor view packs pull in `Microsoft.AspNetCore.App`, which has **no runtime pack for `maccatalyst`/`ios`** (`NETSDK1082`) — so a Mac/iPhone build can't use them. The control models (`MeshWeaver.Layout`) and the layout-area streams (`MeshWeaver.Data`) are renderer-agnostic and reused unchanged; only the **view layer** is re-implemented natively, in `src/MeshWeaver.Maui` (`MauiViewPack.cs`).

See also [Local-First Client & Bootstrap](../LocalFirstClient).

## The rendering model — a recursive tree via a model→view registry

The view pack is a **typed model→view registry** — the same idea as WPF/MVVM `DataTemplate` (ViewModel type → View), Prism regions, or the MVP pattern. In Blazor a layout-area reference is a `<div class='layout-area'>` placeholder that the framework **hydrates** into a component (markup → component). MAUI has **no markup hydration**; instead you compose the view tree **directly**:

```csharp
MauiViewRegistry.Register<TControl, TView>()        // the model→view map (a DataTemplate)
MauiControlRenderer.RenderControl(control, stream, area)   // resolves the View for a UiControl model
MauiControlRenderer.RenderArea(stream, area)        // resolves the control AT an area pointer, then RenderControl
```

Rendering is therefore a **recursive tree**: a node is `(UiControl model) → (View from the registry) → (which may itself be a LayoutAreaView that opens another area and recurses)`. `LayoutAreaControl → LayoutAreaView` is the **recursion point** — embedding one area inside another is just another registry lookup.

Pipeline for one area:

```
GetRemoteStream(address, LayoutAreaReference(area))   // the node hub's area stream (synced copy)
  → RenderArea(stream, area)
      → GetControlStream(area)            // the control at the area's pointer
          .RetryAreaWithBackoff(...)      // shared helper — retry transient area errors (see below)
      → RenderControl(control)            // registry → the native View
          → container controls recurse:   RenderArea(stream, childArea) per NamedArea
```

`RenderArea` shows a spinner until the control arrives, then (15–20 s) **times out to a visible error** rather than spinning forever — so an unresolvable area is diagnosable.

## Markdown and the `@@` operator

Markdown is **not** homebrewed. `MarkdownView`/`HtmlView` build HTML with the **official** `MeshWeaver.Markdown` Markdig pipeline (`MarkdownExtensions.CreateMarkdownPipeline`, the same one `MemexConfiguration`/Blazor use) and render it in a **plain `WebView`** (`HtmlWebViewSource` — NOT BlazorWebView, so no AspNetCore). A small injected CSS sets a system sans-serif font (fixing serif headings) and dark colors.

The `@@` operator (inline layout-area injection; `@@path` vs `@path` link) is **recognized + generated** correctly — `LayoutAreaMarkdownParser` emits a `layout-area` element, verified by `test/MeshWeaver.Markdown.Test/MauiMarkdownAtAtOperatorTest.cs`. A **static WebView can't hydrate** that placeholder into a live area, so the native equivalent of Blazor's div-hydration — **the segmenter** — is now implemented:

> `MarkdownView`/`MauiCollaborativeMarkdownView` **segments** the rendered HTML: `MarkdownViewLogic.SplitLayoutAreaRefs` (pinned by `test/MeshWeaver.Markdown.Test/MarkdownSplitLayoutAreaRefsTest.cs`) splits it into prose runs (a small WebView per run) and `@@` embeds; for each embed, `BuildEmbeddedArea` builds a native `LayoutAreaView`, stacking the segments. No div, no hydration — just the model→view tree.

Both embed forms resolve:
- **Pre-resolved** (`@@/Acme/area/Search` → `data-address`/`data-area`) → `LayoutAreaView` over that address directly.
- **Bare path** (`@@Cession/MotorXL` → `data-raw-path`, already absolutised against the authoring node at parse time) → resolved exactly as Blazor's `PathBasedLayoutArea`: `IPathResolver.ResolvePath` matches the longest existing node prefix to an `Address`, `LayoutAreaMarkdownParser.ParseAreaAndId` splits the remainder into area/id, then the same `LayoutAreaView` renders it (reactive — `Subscribe`, resolve once, never await).

The segment-level wiring is done; an embedded area's **own nested sub-areas** still ride the keystone below (the injected `LayoutAreaView` renders its top control but lazily-rendered sub-areas don't resolve across the sync boundary).

## 🔑 The keystone gap — nested/remote sub-areas don't resolve

**Symptom:** a remote framework area (e.g. the user's `Activity` home) renders its **top control** (banner + tab bar) but its **nested sub-areas** (the selected tab's `MeshSearch`, an embedded composer) spin forever, then time out. No sub-area query ever appears in the log.

**Cause:** the area lives on the **`device-user` node hub** (a separate per-node hub, even in-process). The MAUI client reads it via `GetRemoteStream` — a **synced *copy*** of that hub's store. The node hub renders nested sub-areas **lazily** — only when something subscribes to them **on that hub's own stream**. Blazor works because its `NamedAreaView` subscribes on the **same in-process stream** the node hub writes to, triggering the lazy render. The MAUI client's `GetControlStream(subArea)` reads the **local synced copy**; that read never travels back to the node hub, so the lazy render is never triggered and the sub-area control is never produced.

This is why `RetryAreaWithBackoff` (the shared retry Blazor's `NamedAreaView` uses, now also in MAUI's `RenderArea`) **resolves the top level** (it errors transiently and retries) but **cannot fix the nested areas** (they never *emit* and never *error* — there's nothing to retry).

**The fix (a framework change to the layout/sync layer), one of:**
1. **Render sub-areas eagerly** for remote/synced consumers — the node hub renders the area's full sub-tree into the synced store when the area is requested. *(More tractable.)*
2. **Propagate the sub-area subscription back** to the node hub — the remote stream requests the named area, the way Blazor's in-process subscription does.

This single fix unblocks **the framework home, the `MeshSearch` catalog, AND live recursive `@@` embedding** — they all route through the same nested-area resolution. Until it lands, the home runs on a **native hand-built dashboard** (`ActivityDashboardView`: a tab strip + `NodeCardListView` cards over `hub.GetQuery`, a composer, error-surfacing) which works today.

## The declarative catalog (ready, pending the keystone)

The home/user page is authored declaratively in the framework (`UserActivityLayoutAreas.BuildOwnerDashboard`): a `Controls.Markdown` banner (no `Controls.Html` hack) + a **fluent catalog** + the `ThreadComposer` area. The fluent catalog is `MeshWeaver.Layout/CatalogExtensions.cs`:

```csharp
Controls.Tabs
    .WithMeshSearch("Threads", @namespace: "*/_Thread", scope: "descendants", nodeType: "Thread", createNodeType: "Thread")
    .WithMeshSearch("Last Viewed", query: "sort:LastViewed-desc")
```

Each `.WithMeshSearch(...)` adds a labelled tab (skinned `TabsControl`) whose content is a `MeshSearchControl` (with built-in create/empty-state). Both Blazor and MAUI render the resulting `TabsControl` — MAUI's `TabsView`/`MeshSearchView` are implemented; they light up as the home the moment the keystone resolves.

## Decisions / constraints (why it's built this way)

- **No `BlazorWebView`** — it drags in `Microsoft.AspNetCore.App` → `NETSDK1082` on maccatalyst/ios. A plain `WebView` for HTML/markdown is fine (no AspNetCore).
- **DevExpress.Maui is NOT viable** here — though "free", the nuget.org package runs a **license/trial check that validates online** and crashes offline (`SIGABRT`, "not connected to internet") on first control use. The local client is offline-first. Native menus use `DisplayActionSheet` / a lightweight custom dropdown overlay instead — zero dependency, offline-safe. The only zero-licensing-risk library is the MIT `CommunityToolkit.Maui`.
- **No server icons offline** — menu items carry `/static/NodeTypeIcons/*.svg` server paths; loading them as a network `Image` crashes offline. Skip URL/path icons; render emoji/text glyphs only.
- **Menus are provider-driven + read via `hub.GetMenu(context)`** (the shared `MenuStreamExtensions`), not replicated per renderer — see [Node Menu](../../GUI/NodeMenu).
- **New thread = composer-first** — "New thread" opens *only* a composer (no premature node); the thread is created **from** the composer on submit (`hub.StartThread(firstMessage)` → `ThreadNodeType.BuildThreadNode`), with the id autogenerated (`_Thread/{id}`) and the title/description summarised by the agent as the round runs.

## See Also

- [Local-First Client & Bootstrap](../LocalFirstClient)
- [Node Menu](../../GUI/NodeMenu) — the provider-driven menu + `hub.GetMenu`
- [Asynchronous Calls](../AsynchronousCalls) — the reactive `IObservable` rules the view pack follows

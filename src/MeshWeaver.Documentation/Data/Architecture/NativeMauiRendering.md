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

The segment-level wiring is done. An embedded area's **own within-host nested sub-areas** DO resolve over the synced stream (proven below); a **cross-host** embed (a child pointing at a *different* address) is handled by the injected `LayoutAreaView` opening its own per-address remote stream.

## 🔑 The keystone — what was claimed vs. what is proven

**The original claim (now falsified for within-host areas):** a remote framework area renders its **top control** but its **nested sub-areas** spin forever, because the node hub renders sub-areas **lazily** (only on a subscription to its *own* stream) and the MAUI client's read of the `GetRemoteStream` synced *copy* never travels back to trigger that lazy render.

**What the code actually does (and a deterministic test proves):** a container renders its child areas **EAGERLY and RECURSIVELY** at parent-render time — `ContainerControl.Render` iterates its renderers and calls `host.RenderArea` for every child, landing each at `/areas/{parent}/{child}` in the SAME store; an async (data-driven) child lands via a later `UpdateArea` Full. Both flavours are carried to the synced remote copy. So the documented "lazy render never triggers across the sync boundary" is **not** the cause for within-host nesting:

- `test/MeshWeaver.Layout.Test/MauiViewDataPathTest.cs` — a container's **one-level** children all resolve via `GetRemoteStream` + `GetControlStream`.
- `test/MeshWeaver.Layout.Test/MauiNestedSubAreaSyncTest.cs` — a **two-levels-deep** nested container (`A/Inner/Leaf`) AND an **async** sub-area (emitted on the thread pool, after the initial Full) BOTH resolve over the same remote-stream path the MAUI `NamedAreaView`/`RenderArea` use. No hang.

These run the EXACT Blazor-agnostic data path the native views consume (no Xcode/MAUI runtime), so a regression in what the views would render is caught in CI.

**What genuinely remains (needs a running maccatalyst app or a two-host repro — not yet pinned):**
1. **On-device render verification** — the data path resolves the control tree; whether every native `MauiView` paints it correctly is a runtime check (no UI access from CI).
2. **Cross-host embedded areas** — a sub-area that is a `LayoutAreaControl` pointing at a *different* node hub renders by opening a separate per-address remote stream (`LayoutAreaControlView` → `LayoutAreaView`); a within-host test doesn't cover the two-hub round-trip, and any residual "spins forever" symptom most likely lives here (or in an access gate on the sub-area), NOT in within-host lazy rendering. A two-host data-path test is the next repro to add before any sync-layer change.

The home currently runs on a **native hand-built dashboard** (`ActivityDashboardView`: a tab strip + `NodeCardListView` cards over `hub.GetQuery`, a composer, error-surfacing) which works today; the declarative catalog below is wired and, given the proven within-host sub-area sync, is a candidate to replace it once the on-device render is verified.

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

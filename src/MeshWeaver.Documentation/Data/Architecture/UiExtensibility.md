---
Name: UI Extensibility
Category: Architecture
Description: The three lanes for shipping UI — in-mesh layout-area source, compiled view packs, and core — the seams each one plugs into, the DI layering, and the limits.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M17.5 14v7M14 17.5h7"/></svg>
---

MeshWeaver UI has exactly one contract: a **layout area emits a tree of typed controls**, and every
renderer — Blazor Server, the React shells (portal-next and React Native), MAUI — draws that tree.
Extending the UI therefore never means "add a page to the portal". It means one of three things,
and picking the right one is most of the work. This page is the architecture of that choice; the
author-facing rules for making a view render on every client are in
[Writing UI for Every Renderer](/Doc/GUI/CrossRendererAuthoring), and the hands-on layout-area
procedure is the [/layout-area](/Skill/layout-area) skill.

## The three lanes

### Lane 1 — layout-area source in a content plugin (the default)

UI ships as **in-mesh C# source** (`Source/*.cs` under a NodeType), compiled at runtime in the
portal and delivered by a content plugin — the same way `Store`, `Chess`, and `Publish/Slide` ship
interactive UIs of four-digit line counts with zero compiled components. This is the default
because it is the only lane that is installable, updatable, and removable **without a deploy**, and
because a control tree renders on the React and native shells for free.

What this lane can carry: layout areas, dialogs, menus, click/edit interactions, data binding —
everything expressible in the controls language. What it cannot carry, by construction:

- **JS interop or third-party component libraries** — there is no path from in-mesh source to the
  browser's script world.
- **Root-DI services and hosted services** — runtime-compiled node types get per-hub configuration
  (`MeshNode.HubConfiguration`) only; the host container is sealed at startup. Control-plane
  watchers (the exercise-validation shape) stay compiled.
- **Cross-hub type registration** for polymorphic routing.
- **Internal framework surface** — in-mesh compiles see public types only.

If a feature fits inside those limits, it belongs in this lane. Reaching for lane 2 "because C# in
a project is more comfortable" recreates the hard-wiring this page exists to stop.

### Lane 2 — a compiled view pack (a plain class library)

When a view genuinely needs JS interop or a third-party component library, it ships as a **view
pack**: a plain class library holding component types (compiled from `.razor` at the pack author's
build) plus one registration entry point. The portal has essentially no routable content — every
mesh path renders through the catch-all pages — so a pack never touches the router, never adds
pages, and never edits the HTML shell.

`MeshWeaver.Blazor.Radzen` is the reference implementation:

```csharp
// The pack's single hub-side entry point — charts + pivot grid views.
public static MessageHubConfiguration AddRadzenViews(this MessageHubConfiguration config) =>
    config.AddRadzenDataGrid().AddRadzenCharts();

// Each registration is the standard view seam:
public static MessageHubConfiguration AddRadzenCharts(this MessageHubConfiguration config) =>
    config.WithType(typeof(ChartControl))
        .AddViews(layout => layout.WithView<ChartControl, RadzenChartView>());
```

A pack's static assets never go into `App.razor`. Assets that can ride Blazor's per-component
`<HeadContent>` (theme stylesheets) do; classic third-party scripts load through the shared
once-per-document loader (`_content/MeshWeaver.Blazor/assetLoader.js`), gated by the pack's view
base class — `RadzenViewBase` loads `Radzen.Blazor.js` on first interactive render and its views
render their Radzen components only once `AssetsReady` is true. The shell stays pack-free, pages
that never show a chart never fetch Radzen, and a pack is droppable by deleting one registration
line.

Two load-bearing rules for pack authors:

1. **Register before `AddBlazor()`.** View maps are first-match-wins and the core registry's
   default mapping is currently terminal (its fallback never declines), so a pack registered after
   the core mapping is silently dead. The portal's composition does this correctly today; treat it
   as a contract until the planned explicit-priority fix lands.
2. **Never ship copies of contract assemblies.** The pack compiles against the host's
   `MeshWeaver.Layout` / `MeshWeaver.Blazor` and binds to them at load time. A same-named type from
   a second copy is the platform's documented trap-door class — values silently read as absent.

**DI layering.** The only thing a view pack genuinely needs in the *root* container is services its
components `[Inject]` (the Blazor circuit resolves from the host container) and any hosted
services. Everything else — view maps, hub services — rides hub configuration, which per-user
portal hubs re-run on creation. Packs are wired by a `ProjectReference` today; the same entry
points are consumable by boot-time assembly loading (`MeshBuilder.InstallAssemblies`, which folds a
pack's registrations in before the container builds) once that loader is wired to configuration —
the pack does not change between the two.

### Lane 3 — core

Core is for **generic renderers of framework controls** (the DataGrid, the form controls, Markdown,
containers) and the shell (navigation, chat, reconnect, auth). The bar for adding to core is "every
deployment wants this and it renders a framework-level control". History says the bar is applied
too loosely: a scan of one month found a new feature registration hard-wired into the portal
composition roughly **every three days**, and one built-in node type was added and then retired in
favour of its plugin within nine days. When in doubt, lanes 1 and 2 are reversible; lane 3 is a
permanent tax on the composition root.

## The seams, in one table

| You want to contribute… | Seam | Registered on |
|---|---|---|
| A renderer for a control | `AddViews(l => l.WithView<TControl, TView>())` | hub configuration (pack entry point) |
| A named area on a node type | `LayoutDefinition.WithView(name, generator)` | the NodeType's configuration — in-mesh or compiled |
| Default areas on every node | `AddDefaultLayoutAreas()` composition | core only — extending it puts your area on *every node in the mesh*; prefer a NodeType-scoped area |
| A left-rail navigation for a node family | `INodeNavigationProvider` | DI singleton (pack) — claimed by node shape at render time |
| A settings tab | `AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(...))` | hub configuration (pack or plugin hub config) |
| Node-menu entries / presentation | `AddNodeMenuItems(...)`; menu presentation is editable data (`MenuPresentationOverlay`) | hub configuration / mesh data |
| A home-screen tab | a `HomeTab` node | mesh data — no code at all |
| Hub behaviour for a node type | `MeshNode.HubConfiguration` | works from in-mesh plugins at runtime |
| Root services + nodes from a DLL | `MeshNodeProviderAttribute` + `MeshBuilder.InstallAssemblies` | boot time only |

## What cannot be extended today

Honesty section — these are the known walls, so nobody burns a day rediscovering them:

- **HTTP endpoints.** There is no endpoint-contribution seam; `app.Map*()` calls live in the portal
  composition. (This is what keeps the course-asset endpoint compiled in the portal.)
- **Runtime `.razor` from mesh content.** The in-mesh compile pipeline is C#-only — there is no
  Razor engine at runtime, and collectible recompiles would fight the Blazor renderer's
  type-identity caching. Precompiled packs are the answer, not runtime Razor.
- **Post-start pack loading.** View-map lists are consulted when a hub builds, but the *sources* of
  those lists are frozen at startup today. The mutable pack registry that lifts this is planned;
  until then packs load at boot.

## Rolling out to deployments

An extension is not shipped when it merges — it is shipped when instances run it. The path differs
by lane, and platform configuration decides both:

- **Lane 1 (content plugin)**: the plugin reaches every instance through the registry —
  [Plugin Registry](/Doc/Architecture/PluginRegistry) covers grants and tokens,
  [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) the change path, and
  [Plugin Update On Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild) the auto-update
  opt-in. A plugin listed in an instance's `PluginCatalog:InstallByDefault` installs unattended on
  first boot — which is how a **new instance** gets its UI without a human clicking Install.
- **Lane 2 (view pack)**: today the pack rides the image, and whether it *activates* is platform
  configuration — see [Feature Flags](/Doc/Architecture/FeatureFlags) for the `Features:*` surface
  the composition consults. Once boot-time loading is wired, the pack list itself becomes per-
  deployment configuration on the same surface.
- **Standing up a new instance end to end** — configuration order, secrets, DNS/TLS, plugin
  wiring — is [Deployment](/Doc/Architecture/Deployment) (index),
  [Deployment Options](/Doc/Architecture/DeploymentOptions), and for the shared cluster
  [AKS Deployment](/Doc/Architecture/DeploymentAKS). Wire the plugin grants and
  `InstallByDefault` list **as part of instance provisioning**, not as an afterthought — an
  instance provisioned without them boots with a bare portal and no path to its UI.

Related pages: [User Interface](/Doc/Architecture/UserInterface) ·
[Layout Areas](/Doc/GUI/LayoutAreas) · [GUI Data Binding](/Doc/GUI/DataBinding) ·
[Plugins](/Doc/Architecture/Plugins) ·
[Writing UI for Every Renderer](/Doc/GUI/CrossRendererAuthoring)

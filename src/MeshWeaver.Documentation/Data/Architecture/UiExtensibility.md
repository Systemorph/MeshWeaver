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
portal hubs re-run on creation.

**Activation is a module list, not a compiled call.** Each pack carries an assembly-level
`MeshNodeProviderAttribute` whose `HubConfigurations` apply the pack's entry point and whose
`ModuleDefinition` node registers the DI half — `RadzenViewPackModuleAttribute` (views +
`AddRadzenServices`), `AnalysisViewPackModuleAttribute`, and `GoogleMapsViewPackModuleAttribute`
(views + the `GoogleMaps` options binding). Listing the DLL under `Modules:Assemblies` is the
complete activation: `MeshBuilder.InstallAssemblies` folds the attribute's registrations in before
the container builds, so dropping a line drops the pack (its controls fall to the fallback slot).
The `ProjectReference` from the portal remains only so the DLL and its static assets ride the
publish output — the portal's *code* makes no registration call.

### Lane 3 — core

Core is for **generic renderers of framework controls** (the DataGrid, the form controls, Markdown,
containers) and the shell (navigation, chat, reconnect, auth). The bar for adding to core is "every
deployment wants this and it renders a framework-level control". History says the bar is applied
too loosely: a scan of one month found a new feature registration hard-wired into the portal
composition roughly **every three days**, and one built-in node type was added and then retired in
favour of its plugin within nine days. When in doubt, lanes 1 and 2 are reversible; lane 3 is a
permanent tax on the composition root.

### Lane 2b — a plugin contributing views at runtime

A view pack is compiled into the image. A **plugin** is not: its assembly is compiled and loaded at
NodeType activation, long after the layout client was configured. `WithPortalConfiguration` is how it
reaches the portal hub anyway.

The portal hub is a different hub from the plugin's own — one per browser circuit — so returning a
modified config cannot reach it. The delegate is routed instead:

```csharp
// A NodeType's `configuration` lambda. It configures THIS node's hub; the portal is elsewhere.
config => config
    .WithType(typeof(HeatmapControl))
    .WithPortalConfiguration(portal => portal
        .WithType(typeof(HeatmapControl))
        .AddViews(layout => layout.WithView<HeatmapControl, HeatmapView>()))
```

`HeatmapControl` and `HeatmapView` come from the plugin's own assembly, delivered by its bundle
(see [PluginPackaging](../PluginPackaging)). From the portal's side nothing is special — it is the same
`WithView` seam `MeshWeaver.Blazor.Radzen` uses.

Three properties worth knowing, because each is invisible when it bites:

- **Re-registration replaces.** The contribution is keyed by the plugin hub's address, so a
  recompile — which mints a new collectible `AssemblyLoadContext` — replaces the previous delegate
  instead of stacking one. Stacking would pin the old ALC against unload *and* put two CLR
  identities of the same view type into one portal.
- **It applies to the NEXT portal hub.** A portal hub is configured once, at circuit creation, so a
  plugin installed mid-session takes effect on the viewer's next page load.
- **A dropped contribution is logged.** On a headless host (the sidecar, a test mesh with no layout
  client) there is no portal to configure and the contribution is discarded — with a warning naming
  the address, because otherwise it presents as a view that simply never renders.

Note the type registration on **both** sides: the control travels between hubs, so each end needs it
in its `TypeRegistry` or it degrades to an untyped `JsonElement` and the area renders empty.

## The seams, in one table

| You want to contribute… | Seam | Registered on |
|---|---|---|
| A renderer for a control | `AddViews(l => l.WithView<TControl, TView>())` | hub configuration (pack entry point) |
| A named area on a node type | `LayoutDefinition.WithView(name, generator)` | the NodeType's configuration — in-mesh or compiled |
| Default areas on every node | `AddDefaultLayoutAreas()` composition | core only — extending it puts your area on *every node in the mesh*; prefer a NodeType-scoped area |
| A left-rail navigation for a node family | `INodeNavigationProvider` | DI singleton (pack) — claimed by node shape at render time |
| A GLOBAL settings tab (`/_Setting/GlobalSettings/{id}`) | a `UiContribution` node (`Context: Settings`) pointing at a layout area — or compiled `AddGlobalSettingsMenuItems(...)` for content that needs code | mesh data / hub configuration |
| A PER-NODE settings tab (`/{nodePath}/Settings/{id}`) | a `UiContribution` node (`Context: NodeSettings`) — or compiled `AddSettingsMenuItems(...)` | mesh data / hub configuration |
| Node-menu entries / presentation | a `UiContribution` node (`Context: Node`/`Mesh`/`AI`/`SidePanel`/…) — or compiled `AddNodeMenuItems(...)`; presentation stays editable data (`MenuPresentationOverlay`) | mesh data / hub configuration |
| A whole NEW top-bar menu | a `UiContribution` node (`Context: TopBar`) declaring the dropdown; entries target its key | mesh data — no code at all |
| A home-screen tab | a `HomeTab` node | mesh data — no code at all |
| Hub behaviour for a node type | `MeshNode.HubConfiguration` | works from in-mesh plugins at runtime |
| Root services, nodes, hub + per-node-hub configuration, or a whole builder extension from a DLL | `MeshNodeProviderAttribute` (`Nodes` / `HubConfigurations` / `DefaultNodeHubConfigurations` / `BuilderConfigurations`) + `MeshBuilder.InstallAssemblies` | boot time only (`Modules:Assemblies`) |
| Portal-hub config from a plugin (incl. views) | `WithPortalConfiguration(portal => …)` | the plugin's own hub configuration — at runtime |

## Menus and settings tabs as data — `UiContribution` nodes

The composition-first lane: a **menu entry, settings tab, or whole top-bar menu is a mesh node**
(`nodeType: UiContribution`), so a plugin ships it as pack content and an admin edits it live —
no build, no image, no rollout. One live query per silo (the `UiContributionCatalog`) feeds every
per-node hub's menu aggregation; installing or updating a contribution changes every open menu
reactively.

**The security boundary.** Visibility is enforced by COMPILED code against a CLOSED vocabulary —
a contribution can only ever NARROW its own visibility, never widen anything, and it never
introduces a render surface: it points at a layout AREA that renders through the ordinary layout
pipeline and its own access gates. Anything beyond the vocabulary stays in code.

The full field surface:

| Field | Meaning |
|---|---|
| `Context` | Which menu: `Node` (default), `Mesh`, `Settings` (GLOBAL settings page), `NodeSettings` (PER-NODE settings page), `AI`, `GitHub`, any registered context — or `TopBar` (below) |
| `Area` | The layout area the entry opens (settings: embedded into the pane; menus: the link target) |
| `Href` | Optional explicit link overriding the derived area URL — **portal-internal only** (a single-slash-rooted path like `/search?…`; schemes and `//host` forms are discarded by the compiled gate and the entry falls back to its area link) |
| `Label` / `LabelKey` | Display text; the key resolves against the shared localization catalog |
| `Icon` | String icon — settings tabs parse it via `Icon.Parse` (Fluent name/SVG/URL/emoji); menus render emoji or image URLs |
| `Tooltip` / `TooltipKey` | Hover text (menus and top-bar buttons) |
| `Order` | Sort position (default 100 — after the built-ins) |
| `Group` / `GroupKey` / `GroupIcon` | Settings-tab grouping (entries sharing a group nest under its header) |
| `Keywords` | Extra SEARCH terms for the per-node settings search box — what is *inside* the tab, not its name. Consumed by `NodeSettings` only (the global page has no search); omitting them on a migrating tab removes it from search silently |
| `RequiredPermission` | Checked against the viewer's LIVE effective permission on the anchoring node, floored at `Read`; anonymous sees nothing |
| `Gates.NodeTypes` | Suffix-aware node-type filter (`"Slide"` matches `Publish/Slide`) |
| `Gates.ExcludePartitionRoot` | Never on ANY user's home — the built-in suppression's own predicate |
| `Gates.ExcludeViewerHome` | Never on the VIEWER'S OWN home (the anchoring path is the viewer's partition key). Strictly narrower than `ExcludePartitionRoot`, not a replacement: declare both when both apply |
| `Gates.SyncedOnly` | Only while the node still participates in sync (`SyncBehavior.Include`) — the shape the "Stop synchronization" action needs. The inverse is deliberately absent; the vocabulary stays closed |
| `Gates.AdminOnly` | Platform admins only (`hub.IsGlobalAdmin()`, reactive) |

Every gate SUBTRACTS, and they are evaluated in ONE compiled place (`UiContributionProjection.PassesNodeGates`)
so the node menu and the per-node settings page can never drift on what a gate word means.

**Settings tabs — TWO surfaces, TWO keys.** The portal has two settings pages, and each answers
its own context key:

| Surface | Route | Context | Projects into |
|---|---|---|---|
| Global settings | `/_Setting/GlobalSettings/{id}` | `Settings` | `GlobalSettingsMenuItemDefinition` |
| Per-node settings | `/{nodePath}/Settings/{id}` | `NodeSettings` | `SettingsMenuItemDefinition` |

They are **not one key**, deliberately. A single key would list every tab on both pages — the seven
platform tabs seeded for the global page would appear on every node's settings page, which is a
visible regression rather than a migration — and it would make the two surfaces impossible to gate
independently, which is the property the contribution lane exists for. A tab that genuinely belongs
on both is two contributions, and says so.

On both surfaces the contributed area is embedded into the pane's styled stack, and the tab id is
the NODE id, so a `/…/Settings/{id}` deep link stays stable when a compiled tab migrates to a
same-named seed. The platform's own global tabs ship this way — What's New / About / Privacy plus
the admin tabs Invitations / Inbox / Updates / Published to the web / Token Usage
(`Admin/UiContribution/*` seeds); every admin tab's AREA re-asserts the admin gate, because an area
is directly URL-addressable and `Gates.AdminOnly` only hides the menu entry.

🚨 **The per-node lane never filters on permission and takes none.** It stamps
`RequiredPermission` (floored at `Read`) onto the definition and lets
`SettingsMenuItemsExtensions.FilterByPermission` apply it at the render fold, against the LATEST
permission value. Filtering inside the provider stream would bake a permission SNAPSHOT into a
long-lived chain — the #1962 defect, where an early `Permission.None` seed stays subscribed and
later re-renders the menu with every entitled tab silently missing. The Read floor is what still
makes an anonymous viewer see nothing.

**Whole top-bar menus** (`Context: TopBar`): the contribution declares a NEW dropdown — its `Area`
names the menu's own context key, `Label`/`Icon`/`Order`/`Tooltip` style the button, and its
entries are ordinary contributions targeting that key. The gates apply to the declaration itself
(an `AdminOnly` menu disappears wholesale), and a menu with no visible entries renders nothing.
The AI menu's catalog entries (Threads / Models / Tiers / Providers / Agents / Skills) are seeded
contributions in the `AI` context; only imperative click-action entries (New thread, the side
panel's new-chat/history/fullscreen) stay compiled — behavior, which the closed vocabulary
deliberately cannot express.

**Seeding**: platform-static seeds ride `MeshBuilder.AddMeshNodes(...)`
(`AddPlatformSettingsTabContributions`, `AddAiMenuContributions`); a plugin just ships
`UiContribution` nodes as pack content under its own namespace.

### 🚨 A contribution nobody consumes is DARK — check it statically

A contributed entry naming a `Context` nobody declares renders **nowhere**, and nothing anywhere
says so: no error, no warning, not even an area-not-found placeholder. Six shipped entries were
dark for nine days that way. The same silence covers an empty `Area` (dropped before any gate
runs), a non-portal-internal `Href` (discarded, so the entry quietly opens the derived area URL
instead), a node whose `NodeType` is not `UiContribution` (the catalog query never returns it), a
label with no `LabelKey` (English for every German viewer), and two seeds at the same path (the
catalog is keyed on path — one silently replaces the other).

`UiContributionSeedValidation.Validate(seeds, options, additionalContexts, registeredAreas)` is the
pure check for all six; it folds in the context keys a `TopBar` declaration in the same set
introduces. Core pins its own seed list with it (`PlatformSettingsTabSeedTest`); the Plugins repo's
`scripts/check-menu-contexts.py` is the script form of the same check for content-authored packs.
Any repo seeding contributions from compiled code should call it from a test — with a control arm,
because a check that cannot fail reproduces exactly the silence it exists to break.

## What cannot be extended today

Honesty section — these are the known walls, so nobody burns a day rediscovering them:

- ~~**HTTP endpoints.**~~ No longer a wall: a MODULE contributes routes via
  `MeshEndpointProviderAttribute` (`MeshWeaver.Hosting.AspNetCore`) — authenticated by default,
  loud startup refusal on route collisions; see [Modules](/Doc/Architecture/Modules). Mesh DATA
  (plugins' nodes) still cannot contribute endpoints — trusted compiled code only.
- **Runtime `.razor` from mesh content.** The in-mesh compile pipeline is C#-only — there is no
  Razor engine at runtime, and collectible recompiles would fight the Blazor renderer's
  type-identity caching. Precompiled packs are the answer, not runtime Razor.
- **Razor assets from a runtime-loaded assembly.** A plugin can now register views after startup
  (see below), but an assembly loaded at runtime brings no *static web assets*: an RCL's `wwwroot`
  is served from a build-time manifest at `_content/<lib>/…`, and `.razor.css` is bundled into
  `<project>.styles.css` at build. Neither exists for an assembly the image was not built with, so a
  runtime-contributed view must carry no CSS/JS of its own — inline what it needs, or ship the
  assets in a compiled pack.

## Rolling out to deployments

An extension is not shipped when it merges — it is shipped when instances run it. The path differs
by lane, and platform configuration decides both:

- **Lane 1 (content plugin)**: the plugin reaches every instance through the registry —
  [Plugin Registry](/Doc/Architecture/PluginRegistry) covers grants and tokens,
  [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges) the change path, and
  [Plugin Update On Green Build](/Doc/Architecture/PluginUpdateOnGreenBuild) the auto-update
  opt-in. A plugin listed in an instance's `PluginCatalog:InstallByDefault` installs unattended on
  first boot — which is how a **new instance** gets its UI without a human clicking Install.
- **Lane 2 (view pack)**: the pack rides the image, and whether it *activates* is the
  deployment's `Modules:Assemblies` list — the same lane the AI provider packs and the storage
  boot-packs use. The former `Features:UiPacks:*` flags are gone; drop the DLL's line to drop the
  pack. (Other feature toggles remain on the `Features:*` surface —
  [Feature Flags](/Doc/Architecture/FeatureFlags).)
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

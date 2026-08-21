# The Apps Home

The user home's catalog region is a **tabbed surface** — the phone-home model: what you see first
is what you *use*, not everything the mesh can show you. The tabs, in order:

| Tab | Present when | Contents | Default order |
|---|---|---|---|
| **Shared with me** | the caller has cross-partition grants | modules in OTHER partitions the caller was invited into (#385) | last accessed |
| **Pinned** | the caller has pins | the owner's content shortcuts (`User.PinnedPaths`) | last modified |
| **Apps** | always | the platform's default apps ∪ the owner's installed apps — every app **exactly once** | alphabetical |
| **Spaces** | always | the catalog **without** store items | last accessed |

Every listing tab offers the three sort options (last accessed · last modified · alphabetical).
The whole surface is built by `UserActivityLayoutAreas.BuildHome` (`src/MeshWeaver.Graph`), pure and
unit-tested (`HomeTabsTest`); the reactive shell is `CatalogAreaView`, which combines the config,
the caller's grants, the installed-app records, and the owner node.

## Installed apps — `{user}/_App/{appId}`, a REGULAR node

One node per icon, nodeType **`InstalledApp`**, stored at `{user}/_App/{appId}` as an ordinary
`mesh_nodes` row: deliberately **not a satellite** (no `IsSatelliteType`, no `SatelliteTableMapping`
entry — an unmapped `_` segment routes to the partition table, and the `_` prefix already keeps it
out of the search context). This is the same shape as `{user}/_Memex/AiSettings`.

The content record (`App`, `MeshWeaver.Mesh.Contract`) carries **presence and placement only** —
the `Plugin` path (the app's identity, usually a Store cover), `Order`, an optional `OpenPath`
override, and `Source`. Name, icon, and translations resolve **live** from the plugin node; tile
state is derived at render time. Nothing is copied, so nothing can drift.

> 🚨 **Why the nodeType is `InstalledApp`, not `App`.** A built-in NodeType's definition node claims
> the TOP-LEVEL PATH of its name (`AddMeshNodes`), and `App`/`app` is a name real content uses. A
> static claim at `App` refused node creation at `app`, broke path resolution for `app/…` with
> routing loops, and refused installing any package named App (the static/durable claim collision,
> #1209). Pick collision-improbable names for built-in NodeTypes.

**Writers:** the Store's install flow creates the record when a viewer Gets/Adds an app; removing
the icon deletes the node — never the entitlement. The platform's default apps are **not** written
as nodes at all: they come from config at render time.

## The config — `Admin/HomeConfig`

The admin-editable platform node (`HomeConfigNodeType`, public-read, live-reloading) drives the
whole surface without an image roll:

- **`Style`** — `Tabs` (the default) or `Catalog`, the escape hatch back to the legacy single flat
  list (`BuildCatalog`).
- **`DefaultApps`** — the paths every user's Apps tab starts with (shipped: `Store`, `Doc`,
  `~/Chat`). A **render-time union** with the owner's `_App` records, deduped — no seeding, and an
  admin's edit updates every open home live. An entry starting with **`~/`** declares an AREA on
  the *viewer's own hub* instead of a node path (`~/Chat` → the Threads app at `/{owner}/Chat`),
  rendered as a fixed "dock" tile ahead of the node grid (`BuildSystemAppTile`).
  Until a list-capable edit-form field ships, `DefaultApps` is `[Browsable(false)]` on the generic
  content editor — admins edit it on the node content directly (MCP `patch` on `Admin/HomeConfig`).
- `Scope`, `Render`, `DefaultSort` — unchanged, applying to the Spaces tab (and the legacy list).

**The dedup rule:** an app is represented exactly once. The Spaces tab excludes
`-nodeType:Store/Plugin -nodeType:Store/Catalog` — anything living in the Store is reachable in
the Store and (when installed) on the Apps tab, never listed twice. And no tab embeds its own
search box: every client's chrome already carries the global search, and doubling it is the
two-search-bars problem on the mobile clients.

**The Store itself is an app.** Signed-in users reach it as the 🏪 tile (a config default), not a
header anchor; only the anonymous header keeps a Store link — visitors have no home grid, and the
storefront is the public sales surface.

## Sort semantics — why last-accessed is a two-leg union

`source:accessed` is an **INNER join** on the caller's access log: a pure accessed-sorted query
HIDES anything never opened — a fresh invitation, a never-launched app, the exact items those tabs
exist to surface. The last-accessed sort option is therefore a newline-joined, path-keyed UNION:
the accessed-ranked leg first, a plain leg as completeness fallback (the engine dedupes by path).
Nothing is ever hidden by a sort.

## The Threads app

`/{user}/Chat` (the ChatArea) is the **Threads app** — a vertical rail of the owner's open threads
beside the node-less composer (`BuildThreadsApp`). Each rail row renders on the *thread's own hub*
via the `RailItem` area (`ThreadRailItem`, `MeshWeaver.AI`): the title navigates to the thread, and
an **✕ closes it** through the canonical `MarkThreadDone` — the rail's query excludes
`content.status:Done`, so a closed thread leaves the list reactively while staying searchable and
reopenable. Closing never deletes.

Two render details that matter: the rail is `Flat + MaxColumns(1) + ItemArea` — the `List` render
mode draws its own rows and **ignores** item areas, so the ✕ could never render there; and the
icon-only ✕ carries `ButtonControl.Label` (the aria-label) with the localized `thread.close`.

## What comes next

Store integration lives in the MeshWeaver.Plugins repo (mesh-compiled — invisible to CI): the
install flow writes the `InstalledApp` record after `WriteManifest`, an **Add** action covers
open-only domain plugins, uninstall removes the tile, and `Tile`/`Setup` declarations on
`PluginContent` decide which of the store's items surface as apps at all — the ~20 auto-installed
platform capabilities never mint icons.

## See also

- [Configurable Home & Space Pages](/Doc/GUI/ConfigurablePages) — the `Body` + `@@`-region model the home is built on
- [Mesh Search & Catalogs](/Doc/GUI/MeshSearch) — the search control behind every tab
- [Thread Operations](../ThreadOperations) — the canonical thread mutation surface (`MarkThreadDone` et al.)
- [Localization](../Localization) — why every tab label and sort option resolves off `AccessContext.Locale`

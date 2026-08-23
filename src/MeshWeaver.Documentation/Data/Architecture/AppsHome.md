# The Apps Home

The user home's catalog region is ONE search surface whose **scope tabs** are the phone-home tabs —
what you see first is what you *use*, not everything the mesh can show you. The tabs, in order:

| Tab | Present when | Contents | Default order |
|---|---|---|---|
| **Shared with me** | the caller has cross-partition grants | modules in OTHER partitions the caller was invited into (#385) — minus store items and `User` roots | last accessed |
| **Pinned** | the caller has pins | the owner's content shortcuts (`User.PinnedPaths`) | last modified |
| **Apps** | always | the viewer's OWN `{owner}/_App` records — every app exactly once | alphabetical |
| **Spaces** | always | the catalog **without** store items | last accessed |
| **All** | always | everything the viewer can read, at every depth | last accessed |

Because the scopes live INSIDE one `MeshSearchControl` (`MeshSearchScopeTab`), the search bar is
shared: the typed term survives tab switches and every tab is searchable — including All. The
search input renders on desktop and hides on mobile (the chrome search covers phones); search box
and the ordering controls share one header row. The whole surface is built by
`UserActivityLayoutAreas.BuildHome` (`src/MeshWeaver.Graph`), pure and unit-tested
(`HomeTabsTest`); the reactive shell is `CatalogAreaView`.

## Installed apps — `{user}/_App/{appId}` records, the grid's ONLY data source

One node per icon, nodeType **`InstalledApp`**, stored at `{user}/_App/{appId}` as an ordinary
`mesh_nodes` row: deliberately **not a satellite** (no `IsSatelliteType`, no `SatelliteTableMapping`
entry — an unmapped `_` segment routes to the partition table, and the `_` prefix keeps it out of
the search context). This is the same shape as `{user}/_Memex/AiSettings`.

**The record carries the tile.** The node's `Name`/`Icon` are the tile's display identity (stamped
at materialization; the Store's install flow refreshes them on (re)install), and the `App` content
carries the wiring — `Plugin` (the app's path) or `OpenPath` (an area on the viewer's own hub),
plus `Order` and `Source`. The tile renders through the record's own **`AppTile`** area
(`AppTileLayoutArea`): click opens the app, never the record.

> 🚨 **Why records, not cover nodes.** The first grid queried the Store plugin COVER nodes via a
> top-level path alternation (`path:(Store OR Doc OR …)`). A top-level path has no partition hint,
> so that query fanned out across EVERY partition schema — a multi-second home load. The records
> query names ONE partition (`path:{owner}/_App scope:children`) and paints instantly. When a tile
> needs data of the target node, copy it onto the record at write time — never join at render time.

> 🚨 **Why the nodeType is `InstalledApp`, not `App`.** A built-in NodeType's definition node claims
> the TOP-LEVEL PATH of its name (`AddMeshNodes`), and `App`/`app` is a name real content uses. A
> static claim at `App` refused node creation at `app`, broke path resolution for `app/…` with
> routing loops, and refused installing any package named App (the static/durable claim collision,
> #1209). Pick collision-improbable names for built-in NodeTypes.

**Materialization (write-behind).** No onboarding seeding: on home render, `EnsureAppRecords`
compares what the viewer SHOULD have — the config-declared defaults
(`Admin/HomeConfig.DefaultApps`) plus every install-manifest item with a live `installedPath`
(`{owner}/_Install/{slug}`, read untyped by the manifest's own design) — against the records that
exist, and creates the missing ones fire-and-forget into the viewer's own partition. A config
addition reaches every user's grid on their next home render; the Store's install flow writes and
removes records directly (phase 2).

**Threads is an ordinary app.** The `~/Chat` config entry materializes as the record
`{owner}/_App/Chat` (name *Threads*, `OpenPath = {owner}/Chat`) — a normal tile among the others,
not a special dock. A `~/`-prefixed `DefaultApps` entry always means "an area on the viewer's own
hub" rather than a node path.

## The config — `Admin/HomeConfig`

The admin-editable platform node (`HomeConfigNodeType`, public-read, live-reloading):

- **`Style`** — `Tabs` (default) or `Catalog`, the escape hatch back to the legacy single flat list.
- **`DefaultApps`** — the entries every user's Apps grid starts with (shipped: `Store`, `Doc`,
  `~/Chat`), interpreted by the materializer above. Until a list-capable edit-form field ships it is
  `[Browsable(false)]` on the generic content editor — admins edit the node content directly.
- `Scope`, `Render`, `DefaultSort` — apply to the Spaces/All scopes (and the legacy list).

**The dedup rule:** an app is represented exactly once. The Spaces and Shared-with-me scopes exclude
`-nodeType:Store/Plugin -nodeType:Store/Catalog`; Shared-with-me also excludes `-nodeType:User` — a
grant that resolves to another user's home partition must not list that person's space as shared
content. **The Store itself is an app** (a config default); only the anonymous header keeps a Store
link.

## Sort semantics — why last-accessed is a two-leg union

`source:accessed` is an **INNER join** on the caller's access log: a pure accessed-sorted query
HIDES anything never opened. The last-accessed option on the Shared scope is therefore a
newline-joined, path-keyed UNION — the accessed-ranked leg first, a plain leg as completeness
fallback. On the Apps scope, `source:accessed` is meaningless for records, so that option sorts by
last modified instead.

## The Threads app — a multi-document shell

`/{user}/Chat` (the ChatArea) and EVERY thread's full page render the same
**`BuildThreadsShell`**: a fixed 280px rail of the viewer's open threads beside the main pane
(composer on the app page, the conversation on a thread page). Navigating from the rail to a thread
is REAL navigation — and the destination renders the shell again, so the rail never collapses,
exactly like switching documents in a multi-document window.

Rail rows (`ThreadRailItem`, on each thread's own hub) are title + ✕ **siblings** — never an
overlay: an absolutely-positioned ✕ over a full-width navigation button loses its clicks to the nav
surface. The ✕ closes through the canonical `MarkThreadDone`; the rail's query excludes
`content.status:Done`, so a closed thread leaves the list reactively while staying searchable and
reopenable. Closing never deletes.

## Presentation mode (#1803)

The Shared and Pinned scope queries interpolate viewer paths, so the screen filters them BEFORE the
query is built. The Apps records query is generic — no app path can reach a query string or URL —
so the screen filters **at the tile**: `AppTileLayoutArea.BuildTile` renders nothing for a marked
target. The Spaces/All queries stay untouched and filter where results are painted.

## See also

- [Configurable Home & Space Pages](/Doc/GUI/ConfigurablePages) — the `Body` + `@@`-region model the home is built on
- [Mesh Search & Catalogs](/Doc/GUI/MeshSearch) — the search control behind every scope
- [Thread Operations](../ThreadOperations) — the canonical thread mutation surface (`MarkThreadDone` et al.)
- [Localization](../Localization) — why every tab label and sort option resolves off `AccessContext.Locale`

# The Apps Home

The user home's catalog region is ONE search surface whose **scope tabs** are the phone-home tabs —
what you see first is what you *use*, not everything the mesh can show you. The tabs, in order:

| Tab | Present when | Contents | Default order |
|---|---|---|---|
| **Pinned** | the caller has pins | the owner's content shortcuts (`User.PinnedPaths`) | last modified |
| **Apps** | always | the viewer's OWN `{owner}/_App` records — every app exactly once, as an ICON grid | alphabetical |
| **Spaces** | always | the catalog **without** store items | last accessed |
| **All** | always | everything the viewer can read, at every depth | last accessed |

**Shared with me** is not a tab: cross-partition invitations (#385) are a distinct kind of content,
not another lens on the catalog, so they render as their own titled band BELOW the search surface —
present only when the caller actually has such grants, minus store items and `User` roots, ordered
by last accessed.

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

**The record carries the whole tile.** The node's `Name`/`Icon` are the tile's display identity and
its `MainNode` is the navigation target — the app's path (`Plugin`) or an area on the viewer's own
hub (`OpenPath`). All three are stamped at materialization (the Store's install flow refreshes them
on (re)install); the `App` content carries the wiring (`Plugin`/`OpenPath`/`Order`/`Source`). The
Apps scope renders with **`MeshSearchRenderMode.Icons` + `NavigateToMainNode`**: a phone-home icon
grid painted ENTIRELY from the query rows — no per-record layout area, no hub activation per tile,
no content load — and a click opens the app, never the record.

> 🚨 **Why records, not cover nodes.** The first grid queried the Store plugin COVER nodes via a
> top-level path alternation (`path:(Store OR Doc OR …)`). A top-level path has no partition hint,
> so that query fanned out across EVERY partition schema — a multi-second home load. The records
> query names ONE partition (`path:{owner}/_App scope:children`) and paints instantly. When a tile
> needs data of the target node, copy it onto the record at write time — never join at render time.

> 🚨 **Why no per-record tile area.** The second grid rendered each record through its own `AppTile`
> layout area — one hub activation PER RESULT, the exact per-tile cost the record model exists to
> avoid. If a search result can be painted from the row, paint it from the row.

> 🚨 **Why the nodeType is `InstalledApp`, not `App`.** A built-in NodeType's definition node claims
> the TOP-LEVEL PATH of its name (`AddMeshNodes`), and `App`/`app` is a name real content uses. A
> static claim at `App` refused node creation at `app`, broke path resolution for `app/…` with
> routing loops, and refused installing any package named App (the static/durable claim collision,
> #1209). Pick collision-improbable names for built-in NodeTypes.

**Materialization (write-behind).** No onboarding seeding: on home render, `EnsureAppRecords`
compares what the viewer SHOULD have — the config-declared defaults
(`Admin/HomeConfig.DefaultApps`) plus every install-manifest item with a live `installedPath`
(`{owner}/_Install/{slug}`, read untyped by the manifest's own design) — against the records that
exist, creates the missing ones fire-and-forget into the viewer's own partition, and HEALS records
that still carry the generic icon or no `MainNode` target (name/icon come from the plugin cover
node, fetched in ONE one-shot query off the render path). A config addition reaches every user's
grid on their next home render; the Store's install flow writes and removes records directly
(phase 2).

> 🚨 **The materializer acts only on a REAL records snapshot.** The records observable starts with a
> `null` sentinel — never `[]` — because "not loaded yet" and "no records" must differ: the first
> shipped materializer synthesized an empty start and every fresh home render fired ~20 doomed
> `CreateNode` calls against records that already existed ("Node already exists" storms in Loki —
> and the home lag). A create that still loses a race is logged at Debug, not Warning.

**Threads is an ordinary app.** The `~/Chat` config entry materializes as the record
`{owner}/_App/Chat` (name *Threads*, `OpenPath = {owner}/Chat`, `MainNode = {owner}/Chat`) — a
normal tile among the others, not a special dock, and it REPLACES the old open-threads band on the
default home template (the `area/Threads` area stays registered for authored bodies that embed it).
A `~/`-prefixed `DefaultApps` entry always means "an area on the viewer's own hub" rather than a
node path.

## The config — `Admin/HomeConfig`

The admin-editable platform node (`HomeConfigNodeType`, public-read, live-reloading):

- **`Style`** — `Tabs` (default) or `Catalog`, the escape hatch back to the legacy single flat list.
- **`DefaultApps`** — the entries every user's Apps grid starts with (shipped: `Store`, `Doc`,
  `~/Chat`), interpreted by the materializer above. Until a list-capable edit-form field ships it is
  `[Browsable(false)]` on the generic content editor — admins edit the node content directly.
- `Scope`, `Render`, `DefaultSort` — apply to the Spaces/All scopes (and the legacy list).

**The dedup rule:** an app is represented exactly once. The Spaces scope and the Shared-with-me band
exclude `-nodeType:Store/Plugin -nodeType:Store/Catalog`; Shared-with-me also excludes
`-nodeType:User` — a grant that resolves to another user's home partition must not list that
person's space as shared content. **The Store itself is an app** (a config default); only the
anonymous header keeps a Store link.

## Sort semantics — why last-accessed is a two-leg union

`source:accessed` is an **INNER join** on the caller's access log: a pure accessed-sorted query
HIDES anything never opened. The Shared-with-me band's query is therefore a newline-joined,
path-keyed UNION — the accessed-ranked leg first, a plain leg as completeness fallback. On the Apps
scope, `source:accessed` is meaningless for records, so that option sorts by last modified instead.

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

The Pinned scope query and the Shared-with-me band interpolate viewer paths, so the screen filters
them BEFORE the query is built. The Apps records query is generic — no app path can reach a query
string or URL — so the screen filters **at paint**, keyed by each tile's navigation target (the
record's `MainNode`): a marked app's tile is simply not drawn. The Spaces/All queries stay untouched
and filter where results are painted.

## See also

- [Configurable Home & Space Pages](/Doc/GUI/ConfigurablePages) — the `Body` + `@@`-region model the home is built on
- [Mesh Search & Catalogs](/Doc/GUI/MeshSearch) — the search control behind every scope
- [Thread Operations](../ThreadOperations) — the canonical thread mutation surface (`MarkThreadDone` et al.)
- [Localization](../Localization) — why every tab label and sort option resolves off `AccessContext.Locale`

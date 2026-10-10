# The Apps Home

The user home's catalog region is ONE search surface whose **scope tabs** are the phone-home tabs —
what you see first is what you *use*, not everything the mesh can show you. The tabs, in order:

| Tab | Present when | Contents | Default order |
|---|---|---|---|
| **Pinned** | the caller has pins | the owner's content shortcuts (`User.PinnedPaths`) | last modified |
| **Apps** | always | the viewer's OWN `{owner}/_App` records — every app exactly once, as an ICON grid | last used |
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

## Installed apps — `{user}/_App/{appId}` records, the grid's default data source

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

## Who writes a record — the Store, not the home

**The app lifecycle belongs to the STORE**: it creates the record when a viewer installs an app
(stamping the real `Name`, `Icon` and `MainNode`), refreshes it on reinstall, and deletes it on
uninstall. The home does not participate. Core holds exactly one write, `EnsureDefaultApps`, and it
is a **bootstrap, not a sync**: when a viewer's grid is EMPTY, the platform defaults from
`Admin/HomeConfig.DefaultApps` are created once — otherwise a brand-new home would be a blank
screen with no icon to reach the Store by. A viewer who has any record at all never triggers it.

> 🚨 **Why the home stopped materializing.** It used to read the Store's install manifests
> (`{owner}/_Install/{slug}`) on every render, diff them against the records, back-fill what was
> missing and heal names/icons from plugin cover nodes. That made *every home render a write path*,
> put the Store's model in two places, and cost a cross-schema cover query to learn things the
> Store already knew at install time. Rendering the home is a READ.

> 🚨 **A bootstrap acts only on a REAL records snapshot.** The records observable starts with a
> `null` sentinel — never `[]` — because "not loaded yet" and "no records" must differ: the first
> shipped materializer synthesized an empty start and every fresh home render fired ~20 doomed
> `CreateNode` calls against records that already existed ("Node already exists" storms in Loki —
> and the home lag). A create that still loses a race is logged at Debug, not Warning.

## Order — most recently used first

The grid is ordered the way a phone orders apps: **what you opened last comes first**, with
never-opened apps keeping the query's order behind them. That ordering is applied **at paint**
(`MeshSearchScopeTab.SortByAccess`), from the viewer's own `{viewer}/_UserActivity` satellites —
one cheap single-partition read whose ids are the visited path with `/` replaced by `_`, so a
tile's target maps to its access time by a forward computation and never a reverse lookup.

> 🚨 **Why not `source:accessed`.** It is an INNER JOIN on the access log keyed by the row's OWN
> path, and it would fail twice here: it drops every never-opened app (a freshly installed app
> would be invisible), and it matches nothing anyway — opening an app records a visit to the APP,
> never to the `_App` record that points at it. Ordering is a *sort key*, so it must never be
> expressed as a join that also filters.

The access snapshot arrives after the tiles have painted and re-orders them — deliberately the
second pass: ordering never gates the first paint.

**Threads is an ordinary app.** The `~/Chat` config entry seeds the record
`{owner}/_App/Chat` (name *Threads*, `OpenPath = {owner}/Chat`, `MainNode = {owner}/Chat`) — a
normal tile among the others, not a special dock, and it REPLACES the old open-threads band on the
default home template (the `area/Threads` area stays registered for authored bodies that embed it).
A `~/`-prefixed `DefaultApps` entry always means "an area on the viewer's own hub" rather than a
node path.

## Groups and manual order — drag and drop, iPhone-style

The grid is **rearrangeable**: a viewer drags a tile to a new position, into another group, onto
the *New group* zone (which asks for a name), and renames a group from its header. The arrangement
is **per user** and is stored **nowhere but on the records themselves**:

| Field | Meaning |
|---|---|
| `App.Group` | the section the tile sits in. `null` = never grouped (the Store stamps the package's `category` at install); `""` = the viewer deliberately ungrouped the tile — a value no heal may overwrite. A group exists exactly while a tile carries its name; renaming a group rewrites its members. **The group is a stamped COPY of the category, like the tile's name and icon, and converges the same way** (Plugins, 2026-09-14): the Store's tile refresh fills a missing group AND moves a machine-stamped one when the package is re-categorized; a group the viewer chose is declared by `content.customGroup: true`, which every drag, drop and rename writes — declared, never inferred. |
| `App.Order` | the position inside the group, `1..n`. `0` = never placed: such tiles paint **behind** the placed ones, in the grid's own most-recently-used order, so a freshly installed app lands at the end of its group the way a phone appends a new icon. |

This repo holds the CONTRACT: `BuildAppsBand` declares it — `WithGroupBy(nameof(App.Group))` +
`WithSortable()` on the `MeshSearchControl` (and `Sortable = true` on its one scope). The GUI that
honours it lives with the Blazor layer in **MeshWeaver.Plugins**: its `MeshSearchView` renders a
`Sortable` Icons grid through the reusable `SortableTileGrid` component — native HTML5 drag events
handled by Blazor (`dragstart` / `dragenter` / `drop`), **no JS interop**, so there is no module
to dispose and nothing that can throw *Cannot access a disposed object: JSObjectReference* when a
circuit goes away mid-drag. A platform without that GUI module simply paints the plain Icons grid;
the declaration is inert until a view reads it.

**The view has two LEVELS (Plugins, 2026-09-14).** With a dozen groups the sectioned grid was a
wall of icons and no overview, so the band opens on **categories first**: the overview is one
folder tile per group (a 2×2 miniature of its first apps, its name, its count), an *All* folder, and
the ungrouped tiles loose beside them; a folder opens to that group's tiles alone; *All* is the
full sectioned grid where tiles move between groups. A path above the grid (`Apps › Daily work`) is
the way up, and the **last place is remembered per launcher in the browser** (`localStorage`,
keyed by the band's query so two users on one browser never share it) — read on first render,
before the tiles' query answers, so the viewer returns to where they were with no jump. Nothing of
this is stored on the mesh, and the contract above is unchanged: the folders are the same groups,
the same records, the same writes. The rules and the category taxonomy that motivated it (*Daily
work*, *Administration*) are in the Plugins doc `Store/HomeSurface`.

What the contract asks of any such view: a drop computes the target group's new sequence, renumbers
it `1..n`, and writes **only the records whose `group` or `order` changed** through
`IMeshNodeStreamCache.Update(recordPath, …)` — the ONE mutation API, a field-level merge patch per
record, so a concurrent heal of the same record's icon or name never collides with the viewer's
arrangement. There is no layout document, no `/data` replica and no save button: the grid re-paints
from the same reactive query that painted it, and the records ARE the state. Because the
arrangement rides on the rows, a Sortable grid's projection must include `content` (the row-only
select the plain Icons grid uses would drop it).

> 🚨 **Why not one layout node per user.** A `{user}/_Home` document holding `groups[].apps[]`
> would need an existence gate before every read (a point read of an absent node trips the
> storm-breaker), a seed for every existing user, and a reconciliation pass whenever a tile is
> installed or removed. The records already have all three properties: they exist exactly when
> the tile does, they are per user, and the Store's install/uninstall lifecycle keeps them right.

## The app directory — `Home:AppSource = Directory` (the default)

**The Apps band's default source** (MeshWeaver#6448; MeshWeaver.Plugins `Store/AppsOnTheInstance`,
phase 1a): **the apps a viewer can see, computed from the instance's app roots and the access check,
with no per-user records.** `Home:AppSource` is a closed two-value deployment setting:

| Value | The Apps band reads |
|---|---|
| absent, or `Directory` | **the app directory** (this section) — the image default, so the record-driven fleet, which renders no `Home__AppSource`, runs it with the roll |
| `Records` | the viewer's own `{user}/_App` records (the section above) — an explicit escape hatch while those records still exist |
| anything else | a configuration ERROR: the home logs it at error level, naming the key, and serves the default; the page never fails on a typo |

The Store's, Edu's and the AI engine's per-user home writers read the same key with the same rule,
and follow the RUNNING image's default for an absent value (MeshWeaver.Plugins#3321; the writers' switch itself landed in #3316), so the launcher
and its writers switch together.

| Piece | Where | What it does |
|---|---|---|
| `AppDirectory` | `MeshWeaver.Graph/Apps` | ONE process-wide query, `namespace: is:main content.app:true partitions:all` (an enumeration, id `$app-roots`), shared by every viewer through `IMeshNodeStreamCache` and read as System. Per root, `CheckPermission(viewer, entryPoint, Read)` — the ENTRY POINT, because a gated root is everybody's storefront; an `X/area/Y` entry point is probed at `X`. Plan coverage and audience reach it as ordinary grant/deny rows, so Read is the whole check. Live: a grant, a revoke or an admin change re-emits. |
| Built-ins | `AppDirectory.BuiltIns` | **Settings** (`{viewer}/Settings`) and **Inbox** (`{viewer}/Inbox`) for everyone, **Administration** (`Admin`) for a global admin. Labels are catalog keys (`common.settings`, `inbox.title`, `adminApp.title`). |
| `AppDirectoryCache` | `MeshWeaver.Graph/Apps` | Per viewer, the `Replay(1)` of the live stream. Shared by concurrent subscribers; when the last leaves, kept warm for **5 minutes, sliding** (a returning subscriber resets the clock), then disposed. A faulted computation is evicted at once, never replayed. Its own scheduler; no `IMemoryCache`. |
| `AppDirectoryQueryProvider` | `MeshWeaver.Graph/Apps` | Answers the RESERVED virtual namespace `{user}/_Apps` (`path:{user}/_Apps scope:children nodeType:InstalledApp`) with synthetic rows of the same shape as the `_App` records — content `App` (`Plugin` = app id, `OpenPath` = entry point, `Group`, `Order`), `MainNode` = entry point, name, icon and description from the root. The full query predicate applies (`IMeshNodeQueryMatcher`, the in-memory evaluator), and both query surfaces stay live — a revoked app leaves the snapshot surface too. Only the owner is answered and only the owner's own request may seed; the System synced-query channel is side-effect free. Nothing is stored under `_Apps`. |
| `LauncherArrangement` | `{user}/_Settings/Launcher` | The viewer's placements, `entries: { appId: { group, order, customGroup } }`. Group comes from here, else the root's `Category`. Seeded ONCE from the viewer's `_App` records the first time the directory renders for them — the rows wait for it, one seed in flight per viewer, never repeated after success, retried after a failure, create-if-absent, as the viewer — then written only when the viewer rearranges. A rearrangement over unreadable content fails loudly rather than starting fresh. |

**Rearranging a directory tile** writes the arrangement node, never the virtual row:
`LauncherArrangementPaths.Place(cache, rowPath, group, order, options)`. The launcher view routes a
drop on a `{user}/_Apps/…` row there.

The seeders (`SeedDefaultAppsLogonAction`, `SeedInboxAppLogonAction`) still run; the directory does
not depend on them. Pinned by `AppDirectoryTest` / `AppDirectoryPureTest` (MeshWeaver.Graph.Test).

## The config — `Admin/HomeConfig`

The admin-editable platform node (`HomeConfigNodeType`, public-read, live-reloading):

- **`Style`** — `Tabs` (default) or `Catalog`, the escape hatch back to the legacy single flat list.
- **`DefaultApps`** — the entries a viewer's Apps grid is BOOTSTRAPPED with (shipped: `Store`,
  `Doc`, `~/Chat`). Read only when the grid is empty, so editing it changes what new viewers get,
  not what existing viewers have. Until a list-capable edit-form field ships it is
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

## The Threads app — the chat surface with its native side menu

`/{user}/Chat` (the ChatArea) is ONE `ThreadChatControl` in node-less compact mode with
`ShowThreadNav` on (`BuildThreadsApp` / `ThreadsAppComposer`): a centered start-a-conversation
hero above the compact composer, beside the collapsible **THREADS side menu** — the agentic-app
default view, rendered again on every thread's full page so the navigation never collapses.

The side menu is NATIVE to the Blazor chat view and bound through the synced `GetQuery` cache
(`ThreadQueries.MyOpenThreads` — full thread nodes, content included): New chat, a filter box
("find the thread which does XYZ"; the global mesh search covers semantic lookups), the thread's
hierarchy (ancestors · current · delegation sub-threads), and the viewer's open threads — each
row with its LIVE activity (`ThreadActivity`: **evaluating** while a round runs, a **queued**
badge when input waits in `PendingUserMessages`, **awaiting input** at rest) and an ✕ that closes
through the canonical `MarkThreadDone`. The menu collapses to a slim edge toggle — the same
affordance as the multi-part doc-index rail.

🚨 **Never render a search result through an item area on a foreign hub.** The first Threads app
was an MDI shell whose rail rows delegated to a `RailItem` area on each THREAD's own hub — one
hub activation PER ROW, resolving an area on a hub the page does not own. That shape passes in a
monolith and fails in the distributed portal ("area cannot be found" — the AppTile failure), which
is why the shell, `ThreadRailItem`, and the `RailItem` area were deleted. The menu paints from the
query snapshot; nothing on it resolves a foreign area.

🚨 **Never stretch the compact composer.** The `.no-messages` CSS fill chain is scoped
`:not(.compact-mode)`: unscoped, any page that gives the container a definite height stretched
the compact input into a viewport-height empty box (the old shell's "giant gray void").

Closing a thread goes through the canonical `MarkThreadDone` (the row's ✕ or the thread page's
Mark Done); the menu's query excludes `content.status:Done`, so a closed thread leaves the list
reactively while staying searchable and reopenable. Closing never deletes.

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

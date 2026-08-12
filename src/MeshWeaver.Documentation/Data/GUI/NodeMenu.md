---
Name: Node Menu Items
Category: Documentation
Description: How node types register reactive, permission-aware context menu items — including hierarchical sub-menus and named contexts.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><line x1="7" y1="8" x2="17" y2="8"/><line x1="7" y1="12" x2="17" y2="12"/><line x1="7" y1="16" x2="13" y2="16"/></svg>
---

The portal's node context menu — the cube icon on every node — is fully data-driven. Menu items are registered in the node's `HubConfiguration` as **reactive** providers (`IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>`). A predicate-based renderer subscribes to every provider, merges and sorts their items per context, and pushes the result to the `$Menu` slot in the entity store via `host.UpdateArea` on every emission. The portal reads `$Menu` directly from the layout stream — no separate RPC required.

> 🚨 **The menu is reactive, not a one-time snapshot.** Each provider emits its complete item set and re-emits whenever its inputs change — most importantly, the viewer's effective permissions. A runtime `AccessAssignment` (for example, granting Editor) reaches the menu on the `enriched` permission stream after the synced query catches up; a reactive provider re-emits the moment it does, and the menu self-corrects automatically.
>
> The old `IAsyncEnumerable` + `await foreach … yield break` contract took the **first** permission snapshot and locked it in — baking in whatever had propagated by first render (the access race behind the old `Menu_Editor_ShowsCreateItems` flake). See [Aggregating Providers](../../Architecture/AggregatingProviders).

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 310" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="310" fill="none"/>
  <rect x="20" y="20" width="140" height="50" rx="10" fill="#1e88e5"/>
  <text x="90" y="40" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">Default Provider</text>
  <text x="90" y="56" font-family="sans-serif" font-size="10" fill="#cde" text-anchor="middle">AddDefaultMeshMenu()</text>
  <rect x="20" y="100" width="140" height="50" rx="10" fill="#43a047"/>
  <text x="90" y="120" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">Custom Provider A</text>
  <text x="90" y="136" font-family="sans-serif" font-size="10" fill="#cec" text-anchor="middle">AddNodeMenuItems()</text>
  <rect x="20" y="180" width="140" height="50" rx="10" fill="#5c6bc0"/>
  <text x="90" y="200" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">Custom Provider B</text>
  <text x="90" y="216" font-family="sans-serif" font-size="10" fill="#dde" text-anchor="middle">NodeMenuItemProvider</text>
  <text x="90" y="260" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5" text-anchor="middle">IObservable&lt;Items&gt;</text>
  <text x="90" y="274" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".5" text-anchor="middle">re-emits on perm change</text>
  <line x1="160" y1="45" x2="278" y2="128" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="160" y1="125" x2="278" y2="148" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="160" y1="205" x2="278" y2="168" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="280" y="108" width="150" height="80" rx="10" fill="#f57c00"/>
  <text x="355" y="132" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">CombineLatest</text>
  <text x="355" y="150" font-family="sans-serif" font-size="10" fill="#fee" text-anchor="middle">+ Permission filter</text>
  <text x="355" y="166" font-family="sans-serif" font-size="10" fill="#fee" text-anchor="middle">in each .Select()</text>
  <text x="355" y="182" font-family="sans-serif" font-size="10" fill="#fee" text-anchor="middle">RenderMenus renderer</text>
  <line x1="430" y1="148" x2="498" y2="148" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="500" y="108" width="130" height="80" rx="10" fill="#26a69a"/>
  <text x="565" y="135" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">ImmutableSortedSet</text>
  <text x="565" y="153" font-family="sans-serif" font-size="10" fill="#cef" text-anchor="middle">merged &amp; ordered</text>
  <text x="565" y="169" font-family="sans-serif" font-size="10" fill="#cef" text-anchor="middle">by Order property</text>
  <text x="565" y="185" font-family="sans-serif" font-size="10" fill="#cef" text-anchor="middle">host.UpdateArea()</text>
  <line x1="630" y1="148" x2="698" y2="148" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="700" y="108" width="44" height="80" rx="10" fill="#8e24aa"/>
  <text x="722" y="143" font-family="sans-serif" font-size="10" font-weight="bold" fill="#fff" text-anchor="middle" transform="rotate(-90 722 148)">$Menu</text>
  <line x1="722" y1="188" x2="722" y2="248" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="612" y="250" width="220" height="46" rx="10" fill="#37474f"/>
  <text x="722" y="270" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff" text-anchor="middle">Portal LayoutAreaView</text>
  <text x="722" y="286" font-family="sans-serif" font-size="10" fill="#b0bec5" text-anchor="middle">IMenuItemsProvider → PortalLayoutBase</text>
</svg>

*Reactive menu pipeline: multiple providers combine live into a permission-filtered, sorted menu pushed to the `$Menu` slot on every emission.*

---

## Default Menu Items

`AddDefaultMeshMenu()` — called automatically by `AddDefaultLayoutAreas()` — registers two default providers, one per menu context.

**Node menu** (`DefaultNodeMenuProvider`) — per-node operations, emitted as one flat list. The provider **re-stamps** each item's `Order` so the sections come out in a fixed shape regardless of what each layout area declares:

| Item | Area | Permission | Order | Icon | Notes |
|------|------|------------|------:|:---:|-------|
| Edit | `Edit` | `Update` | 10 | ✏️ | |
| Pin | `Pin` | — | 12 | 🔖 | Viewer-scoped, not permission-gated; hidden on the viewer's own home |
| Move | `Move` | `Delete` | 14 | ➡️ | Requires Delete on the source |
| Copy | `Copy` | `Create` | 16 | 📋 | Duplicates the subtree |
| Delete | `Delete` | `Delete` | 18 | 🗑️ | |
| Files | `Files` | `Read` | 30 | 📁 | |
| Data | `Data` | `Read` | 31 | 🧾 | The raw record, reachable even when Overview is a designed page |
| Versions | `Versions` | `Read` | 32 | 🕘 | |
| Stop sync | `StopSync` | `Update` or `Sync` | 34 | 🔌 | Only on a synced node |
| Recycle | `Recycle` | `Update` | 50 | ♻️ | |

`_separator` entries are inserted at Order 20 and 40 — but **only where both adjacent sections actually carry items**, so a viewer who sees no editable actions never gets a leading divider.

Edit, Move, Copy and Delete are **suppressed on a protected partition root** (a user's home). Deleting that node would wipe the whole partition; `PartitionRootDeletionGuard` blocks it server-side, and the menu keeps it out of reach in the first place. Pin stays.

Threads are **not** in this menu — they live in the dedicated top-bar AI menu (`AiMenuContext`).

**Mesh menu** (`DefaultMeshMenuProvider`) — mesh-level operations, which keep the `Order` their layout area declares:

| Item | Area | Permission | Order |
|------|------|------------|------:|
| Create | `Create` | `Create` | 0 |
| Import | `ImportMeshNodes` | `Create` | 1 |
| Export | `Export` | `Export` | 26 |

Items with a required permission are checked inside the provider. Only items the viewer is permitted to see ever reach the portal.

---

## How the Menu Pipeline Works

When the portal subscribes to the layout stream, the node hub runs the `RenderMenus` renderer. That renderer collects all registered providers, combines their live streams with `CombineLatest`, applies per-provider permission checks in `.Select`, merges the results into an `ImmutableSortedSet` ordered by `Order`, and writes the final list to `$Menu:{ctx}` via `host.UpdateArea`. Every time any provider re-emits, the whole pipeline re-runs and the portal receives a fresh, authoritative menu — no reload needed.

```
Portal (LayoutAreaView)
   │
   │  Subscribes to layout stream
   │  ──────────────────────────────────►  Node Hub
   │                                        │
   │                                        │  WithRenderer(_ => true, RenderMenus)
   │                                        │    → CollectMenuItemStreamsByContext(host, ctx)
   │                                        │    → CombineLatest each provider's IObservable
   │                                        │    → permission checks inside each .Select
   │                                        │    → merged into ImmutableSortedSet by Order
   │                                        │    → host.UpdateArea($Menu:{ctx}, MenuControl)
   │                                        │      on EVERY emission (re-emits on perm change)
   │                                        │
   │  $Menu stream update(s)                │
   │  ◄──────────────────────────────────   │
   │
   │  LayoutAreaView → IMenuItemsProvider
   │  PortalLayoutBase renders items in menu
```

---

## Adding Custom Menu Items

Use `AddNodeMenuItems()` in your node type's `HubConfiguration` to add items beyond the defaults. The provider is a reactive stream — compose the live permission observable with `.Select` and return the **complete** item set per emission. Emit `[]` when you contribute nothing; never return `Observable.Empty`.

```csharp
config => config
    .AddNodeMenuItems((host, ctx) =>
        // GetEffectivePermissions is IObservable<Permission> — re-emits when the viewer's
        // permissions change. .Select off it so the menu re-renders when a role is granted.
        host.Hub.GetEffectivePermissions(host.Hub.Address.ToString())
            .Select(perms => perms.HasFlag(Permission.Update)
                ? (IReadOnlyCollection<NodeMenuItemDefinition>)
                    [new NodeMenuItemDefinition("Suggest", "Suggest",
                        RequiredPermission: Permission.Update, Order: 11)]
                : []))
    .AddLayout(layout => layout
        .WithView("Suggest", MyEditArea.Suggest))
```

Items from `AddNodeMenuItems()` are merged with the defaults and sorted by `Order`.

---

## Hierarchical Sub-Menus

Set the `Children` property to nest items under a parent entry. A provider emits its complete set — including the parent and all its children — on every emission.

```csharp
// Group multiple items under a parent — a provider emits its complete set per emission.
private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> MoreActionsProvider(
    LayoutAreaHost host, RenderingContext ctx)
    => Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>(
    [
        new NodeMenuItemDefinition("More Actions", NodeMenuItemDefinition.GroupArea, Icon: "🧰", Order: 50,
            Children:
            [
                new("Action 1", "Action1Area", Icon: "1️⃣", Order: 1),
                new("Action 2", "Action2Area", Icon: "2️⃣", Order: 2),
            ]),
    ]);
```

### A parent is never activatable

**Any entry carrying `Children` is a sub-menu parent, and no client will activate it** — its own `Area` / `Href` is ignored for activation. That is not a policy invented here; it is what both web component libraries do. FAST's `fluent-menu-item` (Blazor) and Fluent React v9's `MenuTrigger`-wrapped item both *toggle* the sub-menu on click or Enter rather than invoking the parent, so "a parent that also navigates somewhere" is not expressible in either.

Give a pure grouping parent an area from **`NodeMenuItemDefinition.GroupArea(name)`** — `_group:Export` — the sibling of the long-standing `"_separator"`. It makes the wire self-describing: a client that cannot nest can still tell "this is a group, not an action" instead of rendering a dead row that navigates to `/{path}/`.

🚨 **A prefix, not one shared `"_group"` constant.** `Area` is also the stable key the [menu-presentation catalog](../../Architecture/MenuAsData) matches on, and the key another entry names to become a child. One shared sentinel would make every group the same key — an admin could not re-word, re-icon, re-order or hide a *specific* group, and only the first would be addressable as a parent.

### Nesting has two origins, and they compose

A sub-menu can come from **code** (a provider emitting `Children`) or from **data** (a catalog entry's `parent` moving an item under another — [MenuAsData](../../Architecture/MenuAsData)). `RenderMenus` runs the overlay first and normalizes afterwards, so both origins land in the same shape and obey the same rules. A grouping created purely by a node edit sorts and prunes exactly like a compiled one.

The catalog also descends into compiled groups, so grouping entries in code does not make them un-editable: `ExportDocx` sits inside 📦 Export and is still addressable by its own area.

### The aggregator normalizes the tree

Two things happen once, in `RenderMenus` — **after** the overlay, so data-created groupings are covered too:

- **Children are sorted by `Order` at every depth**, with the same comparer the top level uses. Before this, only the top level was sorted and a sub-menu came out in whatever order its provider appended.
- **A `_group:` parent with no surviving children is dropped.** Items are permission-filtered by the *provider*, never by the renderer, so a provider that gates each child individually can legitimately end up emitting a parent whose children all vanished for this viewer — as can a catalog that hides them. Rendering that would give a sub-menu that opens onto nothing. Pruning runs bottom-up, so a group whose only child was itself an emptied group disappears too. A parent with a **real** `Area` of its own survives — it still has somewhere to go, which is also what keeps the overlay's "a dangling `parent` leaves the entry top-level" behaviour intact.

### How each client renders it

Parity across clients means equivalent **capability**, not an identical gesture — the mobile client deliberately differs:

| Client | Rendering |
|---|---|
| Blazor portal (`NodeMenuItemList.razor`) | `<FluentMenuItem MenuItems="…">` → `<fluent-menu slot="submenu">` — FAST's native flyout |
| portal-next / React (`HeaderMenus.tsx` → `MenuEntries`) | nested Fluent v9 `<Menu>` + `<MenuTrigger>` inside the parent `<MenuList>` |
| React Native (`leftMenu.tsx` → `LeftMenuView`) | **drill-down**: tapping a parent replaces the list with its children plus a back control |
| MAUI (`PortalShellPage`) | recursive inline expander |

The web clients get the conventional nested flyout, with the component library supplying roles, `aria-haspopup` / `aria-expanded` and the keyboard model (Enter / ArrowRight to open, ArrowLeft / Escape to close) — **a sub-menu is never hover-only**. The mobile client drills down instead: a flyout that opens a second panel beside the first needs hover and width, and a phone has neither, so exactly one level is on screen at a time, parents carry a `›` chevron, and rows clear the 44 pt touch target.

Nesting depth is unbounded — every renderer recurses. Nothing ships deeper than two levels today.

The built-in node menu does **not** use this pattern. `DefaultNodeMenuProvider` (in `NodeMenuItemsExtensions`, registered alongside `DefaultMeshMenuProvider`) emits Edit, Pin, Move, Copy, Delete and the rest as one **flat** list — no `Children`, no "Actions" parent — grouped by `Order` band and rendered with `_separator` dividers rather than by nesting:

| Order band | Section | Icons |
|---|---|---|
| 10–18 | edit / organize | ✏️ 🔖 ➡️ 📋 🗑️ |
| 27–30 | export / share / approval (contributed by other packages) — **PDF 📄, Email 📤, DOCX 📝** grouped under 📦 Export, Request Approval ✅ | 📦 (→ 📄 📤 📝) ✅ |
| 30–38 | content / history / sync | 📁 🧾 🕘 🔌 🔄 |
| 50 | lifecycle | ♻️ |

🚨 **Every entry needs an `Icon`, and it must be an EMOJI.** The renderer treats a non-emoji value
as an image URL, so a Fluent icon *name* (`"DocumentPdf"`) silently becomes a broken
`<img src="DocumentPdf">` rather than failing. An entry that omits `Icon` altogether renders as a
bare label and reads as a foreign group wedged between the iconed ones — which is exactly what the
export/share block did before it was given 📄 📤 📝. `MarkdownExportMenuTest` asserts this as an
invariant over the whole menu, so a new icon-less entry fails the build rather than shipping.

**Prefer a short label + a translated tooltip over a sentence-shaped label.** The export group is
the worked example: the entries are `PDF`, `Email`, `DOCX` — not "Export to PDF" — with the
explanation moved to `TooltipKey` (`menu.exportPdf.tooltip`, …). This is the AGENTS.md-preferred
shape (language-neutral glyph + short label + translated tooltip) and it shrinks the translation
surface: **`PDF` and `DOCX` are format names and are deliberately identical in every catalog**,
while `Email` → German `E-Mail` is a real word and is translated. Once a label is this short the
tooltip is the only remaining explanation, so `TooltipKey` stops being optional polish — the same
test asserts the group carries one.

Because the aggregator re-sorts every provider's items by `Order`, a plugin's item slots into the right section just by picking a number in that band — which is why the built-in set stays mostly flat. Reach for `Children` when several entries share one sentence: the export block (📄 PDF / 📤 Email / 📝 DOCX) is grouped under a single **📦 Export** parent precisely because "take this document somewhere else" describes all three, and because it was the largest contiguous run in a menu that had grown to roughly fifteen flat rows.

---

## NodeMenuItemDefinition Reference

| Parameter | Type | Description |
|-----------|------|-------------|
| `Label` | `string` | Display text shown in the menu |
| `Area` | `string` | Layout area to navigate to when clicked |
| `Icon` | `string?` | Optional emoji or SVG URL; `null` to skip |
| `RequiredPermission` | `Permission` | Permission the user must have (e.g., `Permission.Update`) |
| `Order` | `int` | Sort order within the menu (lower = earlier) |
| `Href` | `string?` | Optional absolute href — when set, navigates directly instead of using Area |
| `Children` | `IReadOnlyList<NodeMenuItemDefinition>?` | Child items for hierarchical sub-menus. Any entry carrying them is a parent and is never activatable; sorted by `Order` and pruned when empty by the aggregator |
| `Tooltip` | `string?` | Hover tooltip; falls back to `Label` |

Two sentinel `Area` values are reserved: **`NodeMenuItemDefinition.SeparatorArea`** (`"_separator"`) draws a divider, and **`NodeMenuItemDefinition.GroupArea`** (`"_group"`) marks a pure grouping parent. Neither is ever activated.

---

## Advanced: NodeMenuItemProvider

For conditional items that depend on live hub state, register a `NodeMenuItemProvider` delegate directly. The provider must be `IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>` — never `await`, never `Task<T>`:

```csharp
config.AddNodeMenuItems(
    new NodeMenuItemProvider((host, ctx) =>
        CheckSomething(host.Hub)   // IObservable<bool>, re-emits as the condition changes
            .Select(canDoSpecialThing => canDoSpecialThing
                ? (IReadOnlyCollection<NodeMenuItemDefinition>)
                    [new NodeMenuItemDefinition("Special", "SpecialArea", Order: 20)]
                : [])))
```

---

## Named Menu Contexts

By default, items land in the main context menu. You can scope items to a named context — for example, a side panel — by passing a context name to `AddNodeMenuItems`:

```csharp
config.AddNodeMenuItems("SidePanel",
    new NodeMenuItemDefinition("Quick Action", "QuickAction", Order: 1));
```

Named contexts are stored at `$Menu:{context}` and rendered independently from the main menu.

---

## Node Operations

### Export

The Export action packages a node and its entire subtree as a ZIP archive. File formats are chosen by node type:

- **Markdown nodes** → `.md` with YAML front matter
- **Code nodes** → `.cs` as plain C# files
- **Agent nodes** → `.md` with agent-specific YAML
- **All other nodes** → `.json` with polymorphic `$type` content

The exported ZIP mirrors the file-system layout exactly, ensuring round-trip compatibility with Import. Export requires `Permission.Export`, which is included in the Editor and Admin roles but not Viewer.

### Copy

The Copy action duplicates a node and all its descendants to a new namespace. The source node's ID is preserved under the target namespace. Use the "Force" option to overwrite existing nodes at the destination.

### Move

The Move action relocates a node and all its descendants to a new path. It requires Delete permission on the source and Create permission on the target. The operation is atomic per node: descendants move first, then the root.

---

## Generic Navigation

Menu items navigate to their declared `Area` by appending it to the current path (for example, `/TestOrg/Project/Settings`). When `Href` is set, the portal navigates to that absolute URL instead — used for cross-node navigation such as the node-name → NodeType link.

---

## MenuControl and the Entity Store

`MenuControl` is stored at `$Menu` (and `$Menu:{context}` for named contexts) in the entity store, following the same pattern as `DialogControl` at `$Dialog`. It wraps an `IReadOnlyList<NodeMenuItemDefinition>` that may contain hierarchical items with children.

## Reading the menu — `GetMenu` (the read API)

Because the menu lives **in the layout-area stream**, reading it is the same reactive stream tech as `hub.GetQuery` / `GetControlStream` — there is **no renderer-specific menu reader to replicate**. `MeshWeaver.Mesh.MenuStreamExtensions` exposes one common, renderer-agnostic surface (in `MeshWeaver.Mesh.Contract`):

```csharp
// On a layout-area stream you already hold (e.g. inside a view):
areaStream.GetMenu("Node")                       // IObservable<IReadOnlyList<NodeMenuItemDefinition>>

// Hub / workspace shorthand — opens the node's area stream (shared via the remote-stream cache):
hub.GetMenu((Address)nodePath, new LayoutAreaReference("Overview"), "Node")
```

`GetMenu(context)` reads `$Menu:{context}` off the stream (`context: null` → the root `$Menu`) and re-emits whenever the node hub re-renders the menu — e.g. a runtime `AccessAssignment` grants a role. **Both renderers consume this one API**: the native MAUI shell subscribes to `hub.GetMenu(...)` to render the node's actions in its top bar; the Blazor `LayoutAreaView` subscribes to `AreaStream.GetMenu(context)` and forwards items to `IMenuItemsProvider` (a per-circuit scoped bridge to `PortalLayoutBase`, **not** a menu store — and never `static`, which would bleed across users/circuits). The menu *providers* themselves stay where they belong: stateless, idempotently-registered (`TryAddEnumerable`) reactive lambdas on the hub configuration (`AddNodeMenuItems` / `AddDefaultMeshMenu`).

---

## Live Example

The cell below renders the default **node** menu's item set, illustrating the data that backs a typical menu. Note it uses `DataGridControl` — structured data always goes through a control, never a hand-built markdown or HTML string.

```csharp --render NodeMenuDemo --show-code
record MenuRow(string Label, string Area, string Permission, int Order);

var items = new[]
{
    new MenuRow("Edit",      "Edit",     "Update",           10),
    new MenuRow("Pin",       "Pin",      "(none)",           12),
    new MenuRow("Move",      "Move",     "Delete",           14),
    new MenuRow("Copy",      "Copy",     "Create",           16),
    new MenuRow("Delete",    "Delete",   "Delete",           18),
    new MenuRow("Files",     "Files",    "Read",             30),
    new MenuRow("Data",      "Data",     "Read",             31),
    new MenuRow("Versions",  "Versions", "Read",             32),
    new MenuRow("Stop sync", "StopSync", "Update or Sync",   34),
    new MenuRow("Recycle",   "Recycle",  "Update",           50),
};

new DataGridControl(items)
    .WithColumn(new PropertyColumnControl<string> { Property = "label"      }.WithTitle("Label"))
    .WithColumn(new PropertyColumnControl<string> { Property = "area"       }.WithTitle("Area"))
    .WithColumn(new PropertyColumnControl<string> { Property = "permission" }.WithTitle("Permission"))
    .WithColumn(new PropertyColumnControl<int>    { Property = "order"      }.WithTitle("Order"))
```

---

## See Also

- [DataBinding](../DataBinding) — How data flows through controls
- [Editor](../Editor) — The editor control for form rendering
- [Access Control](../../Architecture/AccessControl) — Permission system

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

---

## Default Menu Items

`AddDefaultMeshMenu()` — called automatically by `AddDefaultLayoutAreas()` — registers a standard set of items for every node type.

**Top-level items:**

| Item | Area | Permission | Order | Notes |
|------|------|------------|------:|-------|
| Edit | `Edit` | `Update` | -10 | |
| Files | `Files` | `Read` | 25 | |
| Threads | `Threads` | — | 50 | |
| Versions | `Versions` | `Read` | 55 | |
| Settings | `Settings` | `Read` | 90 | |

**Actions sub-menu** (Order: 95 — contains grouped items):

| Item | Area | Permission | Notes |
|------|------|------------|-------|
| Create | `Create` | `Create` | |
| Copy | `Copy` | `Create` | Duplicates the node subtree to a new location |
| Move | `Move` | `Delete` | Relocates the subtree (requires Delete on source) |
| Import | `ImportMeshNodes` | `Create` | File/folder upload or copy from mesh |
| Export | `Export` | `Export` | Exports subtree as ZIP with native file formats |
| Delete | `Delete` | `Delete` | |

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

Set the `Children` property to nest items under a parent entry. The portal renders the parent with an expand-on-hover sub-menu. A provider emits its complete set — including the parent and all its children — on every emission.

```csharp
// Group multiple items under a parent — a provider emits its complete set per emission.
private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> MoreActionsProvider(
    LayoutAreaHost host, RenderingContext ctx)
    => Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>(
    [
        new NodeMenuItemDefinition("More Actions", "MoreActions", Order: 50,
            Children:
            [
                new("Action 1", "Action1Area", Order: 1),
                new("Action 2", "Action2Area", Order: 2),
            ]),
    ]);
```

The built-in `DefaultMenuProvider` groups Create, Copy, Move, Import, Export, and Delete under the "Actions" parent using exactly this pattern.

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
| `Children` | `IReadOnlyList<NodeMenuItemDefinition>?` | Child items for hierarchical sub-menus |

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

`MenuControl` is stored at `$Menu` in the entity store, following the same pattern as `DialogControl` at `$Dialog`. It wraps an `IReadOnlyList<NodeMenuItemDefinition>` that may contain hierarchical items with children.

The `LayoutAreaView` component monitors the `$Menu` slot and publishes items to `IMenuItemsProvider`, which `PortalLayoutBase` subscribes to for rendering.

---

## Live Example

The cell below renders the `NodeMenuItemDefinition` model as a table, illustrating the data that backs a typical menu:

```csharp --render NodeMenuDemo --show-code
var items = new[]
{
    new { Label = "Edit",     Area = "Edit",    Permission = "Update", Order = -10 },
    new { Label = "Files",    Area = "Files",   Permission = "Read",   Order = 25  },
    new { Label = "Threads",  Area = "Threads", Permission = "(none)", Order = 50  },
    new { Label = "Versions", Area = "Versions",Permission = "Read",   Order = 55  },
    new { Label = "Settings", Area = "Settings",Permission = "Read",   Order = 90  },
    new { Label = "Actions",  Area = "(group)", Permission = "(group)",Order = 95  },
};

var rows = string.Join("\n", items.Select(i =>
    $"| {i.Label,-10} | `{i.Area,-16}` | `{i.Permission,-8}` | {i.Order,4} |"));

MeshWeaver.Layout.Controls.Markdown(
    $"| Label | Area | Permission | Order |\n" +
    $"|-------|------|------------|------:|\n" +
    rows);
```

---

## See Also

- [DataBinding](../DataBinding) — How data flows through controls
- [Editor](../Editor) — The editor control for form rendering
- [Access Control](../../Architecture/AccessControl) — Permission system

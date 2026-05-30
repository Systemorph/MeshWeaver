---
Name: Node Menu Items
Category: Documentation
Description: How node types register custom menu items in the portal's context menu, including hierarchical sub-menus
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="2"/><line x1="7" y1="8" x2="17" y2="8"/><line x1="7" y1="12" x2="17" y2="12"/><line x1="7" y1="16" x2="13" y2="16"/></svg>
---

The portal's node context menu (cube icon) is fully data-driven. Menu items are registered in the node's `HubConfiguration` via **reactive** providers (`IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>`) and rendered during the layout pipeline. A predicate-based renderer subscribes to every provider, merges + sorts their items per context, and pushes the result to `$Menu` in the entity store via `host.UpdateArea` on every emission (same storage slot as `$Dialog`). The portal reads `$Menu` from the layout stream -- no separate RPC needed.

🚨 **The menu is reactive, not snapshot-once.** Each provider emits its *complete* item set and re-emits whenever its inputs change — most importantly the viewer's effective permissions. A runtime `AccessAssignment` (e.g. granting Editor) reaches the menu on the `enriched` permission stream *after* the synced query catches up; a reactive provider re-emits the moment it does, and the menu self-corrects. The old `IAsyncEnumerable` + `await foreach … yield break` contract took the **first** permission snapshot and locked it in — baking in whatever had propagated by first render (the access race behind the old `Menu_Editor_ShowsCreateItems` flake). See [Aggregating Providers](../../Architecture/AggregatingProviders).

# Default Menu Items

`AddDefaultMeshMenu()` (called automatically by `AddDefaultLayoutAreas()`) registers these items for all node types:

**Top-level items:**

| Item | Area | Permission | Order | Notes |
|------|------|------------|-------|-------|
| Edit | `Edit` | `Update` | -10 | |
| Files | `Files` | `Read` | 25 | |
| Threads | `Threads` | None | 50 | |
| Versions | `Versions` | `Read` | 55 | |
| Settings | `Settings` | `Read` | 90 | |

**Actions sub-menu** (Order: 95, contains grouped items):

| Item | Area | Permission | Notes |
|------|------|------------|-------|
| Create | `Create` | `Create` | |
| Copy | `Copy` | `Create` | Copies node subtree to new location |
| Move | `Move` | `Delete` | Moves node subtree (requires Delete on source) |
| Import | `ImportMeshNodes` | `Create` | File/folder upload or copy from mesh |
| Export | `Export` | `Export` | Exports subtree as ZIP with native file formats |
| Delete | `Delete` | `Delete` | |

Items with a required permission are checked inline within the provider. Only visible items reach the portal.

# Hierarchical Menus

Menu items can contain child items via the `Children` property. The portal renders parent items with a sub-menu that expands on hover.

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

The `DefaultMenuProvider` groups Create, Copy, Move, Import, Export, and Delete under an "Actions" parent item using this pattern.

# Server-Side Permission Filtering

Permission checks happen inside reactive `NodeMenuItemProvider` streams subscribed during layout rendering on the node hub. The portal receives only items the user is allowed to see -- no client-side filtering needed. Because the providers are live, the `$Menu` stream re-emits whenever permissions change, so the menu updates without a reload.

```
Portal (LayoutAreaView)
   |
   |  Subscribes to layout stream
   |  ──────────────────────────────────►  Node Hub
   |                                        |
   |                                        |  WithRenderer(_ => true, RenderMenus)
   |                                        |    → CollectMenuItemStreamsByContext(host, ctx)
   |                                        |    → CombineLatest each provider's IObservable
   |                                        |    → permission checks inside each .Select
   |                                        |    → merged into ImmutableSortedSet by Order
   |                                        |    → host.UpdateArea($Menu:{ctx}, MenuControl)
   |                                        |      on EVERY emission (re-emits on perm change)
   |                                        |
   |  $Menu stream update(s)                |
   |  ◄──────────────────────────────────   |
   |
   |  LayoutAreaView → IMenuItemsProvider
   |  PortalLayoutBase renders items in menu
```

# Adding Custom Menu Items

Use `AddNodeMenuItems()` in your node type's `HubConfiguration` to add items beyond the defaults. A provider is a reactive stream — compose the live permission observable with `.Select` and return the **complete** item set per emission (emit `[]` when you contribute nothing; never `Observable.Empty`):

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

## NodeMenuItemDefinition

| Parameter | Type | Description |
|-----------|------|-------------|
| `Label` | `string` | Display text shown in the menu |
| `Area` | `string` | Layout area to navigate to when clicked |
| `Icon` | `string?` | Optional emoji or SVG URL; `null` to skip |
| `RequiredPermission` | `Permission` | Permission the user must have (e.g., `Permission.Update`) |
| `Order` | `int` | Sort order within the menu (lower = earlier) |
| `Href` | `string?` | Optional absolute href -- when set, navigates directly instead of using Area |
| `Children` | `IReadOnlyList<NodeMenuItemDefinition>?` | Child items for hierarchical sub-menus |

## NodeMenuItemProvider

For advanced scenarios, register a provider delegate that emits items conditionally. Compose the live source streams — never `await`; the provider is `IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>`:

```csharp
config.AddNodeMenuItems(
    new NodeMenuItemProvider((host, ctx) =>
        CheckSomething(host.Hub)   // IObservable<bool>, re-emits as the condition changes
            .Select(canDoSpecialThing => canDoSpecialThing
                ? (IReadOnlyCollection<NodeMenuItemDefinition>)
                    [new NodeMenuItemDefinition("Special", "SpecialArea", Order: 20)]
                : [])))
```

## Named Menu Contexts

Register menu items for specific UI contexts (e.g., side panel) using a context name:

```csharp
config.AddNodeMenuItems("SidePanel",
    new NodeMenuItemDefinition("Quick Action", "QuickAction", Order: 1));
```

Named contexts are stored at `$Menu:{context}` and rendered independently from the main menu.

# Node Operations

## Export

The Export action exports a node and its entire subtree as a ZIP archive using native file formats:
- Markdown nodes (`.md`) with YAML front matter
- Code nodes (`.cs`) as plain C# files
- Agent nodes (`.md`) with agent-specific YAML
- Other nodes (`.json`) with polymorphic `$type` content

The exported ZIP is structurally identical to the file system layout, ensuring round-trip compatibility with Import. Export requires the `Permission.Export` flag, which is included in the Editor and Admin roles but not Viewer.

## Copy

The Copy action duplicates a node and all its descendants to a new namespace. The source node's ID is preserved under the target namespace. Use the "Force" option to overwrite existing nodes at the destination.

## Move

The Move action relocates a node and all its descendants to a new path. It requires Delete permission on the source and Create permission on the target. The operation is atomic per node -- descendants are moved first, then the root.

# Generic Navigation

Menu items navigate to their declared `Area` by appending it to the current path (e.g., `/TestOrg/Project/Settings`). When `Href` is set, the portal navigates to that absolute URL instead -- used for cross-node navigation like the node-name -> NodeType link.

# MenuControl

`MenuControl` is stored at `$Menu` in the entity store (same pattern as `DialogControl` at `$Dialog`). It wraps an `IReadOnlyList<NodeMenuItemDefinition>` which can contain hierarchical items with children.

The `LayoutAreaView` component monitors the `$Menu` slot and publishes items to `IMenuItemsProvider`, which `PortalLayoutBase` subscribes to for rendering.

# See Also

- [DataBinding](../DataBinding) - How data flows through controls
- [Editor](../Editor) - The editor control for form rendering
- [Access Control](../../Architecture/AccessControl) - Permission system

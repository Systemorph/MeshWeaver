---
Name: Node Menu Items
Category: Documentation
Description: How node types register custom menu items in the portal's context menu
---

# Node Menu Items

The portal's node context menu (cube icon) is fully data-driven. Menu items are registered in the node's `HubConfiguration` via `IAsyncEnumerable` providers and rendered during the layout pipeline. A predicate-based renderer evaluates all providers and stores results at `$Menu` in the entity store (same pattern as `$Dialog`). The portal reads `$Menu` from the layout stream — no separate RPC needed.

## Default Menu Items

`AddDefaultMeshMenu()` (called automatically by `AddDefaultLayoutAreas()`) registers these items for all node types:

| Item | Area | Permission | Order |
|------|------|------------|-------|
| Create | `Create` | `Create` | 0 |
| Import | `ImportMeshNodes` | `Create` | 1 |
| Edit | `Edit` | `Update` | 10 |
| Threads | `Threads` | None | 50 |
| Settings | `Settings` | None | 90 |
| Delete | `Delete` | `Delete` | 100 |

Items with a required permission are checked inline within the provider. Only visible items are yielded. Only visible items reach the portal.

## Server-Side Permission Filtering

Permission checks happen inside `NodeMenuItemProvider` delegates evaluated during layout rendering on the node hub. The portal receives only items the user is allowed to see — no client-side filtering needed.

```
Portal (LayoutAreaView)
   |
   |  Subscribes to layout stream
   |  ──────────────────────────────────►  Node Hub
   |                                        |
   |                                        |  WithRenderer(_ => true, ...)
   |                                        |    → EvaluateMenuItemsAsync(host, ctx)
   |                                        |    → runs each provider (IAsyncEnumerable)
   |                                        |    → permission checks inline
   |                                        |    → sorted by DisplayOrder
   |                                        |    → stored as MenuControl at $Menu
   |                                        |
   |  $Menu stream update                   |
   |  ◄──────────────────────────────────   |
   |
   |  LayoutAreaView → IMenuItemsProvider
   |  PortalLayoutBase renders items in menu
```

## Adding Custom Menu Items

Use `AddNodeMenuItems()` in your node type's `HubConfiguration` to add items beyond the defaults:

```csharp
config => config
    .AddNodeMenuItems(async (host, ctx) =>
    {
        var perms = await PermissionHelper.GetEffectivePermissionsAsync(
            host.Hub, host.Hub.Address.ToString());
        if (perms.HasFlag(Permission.Update))
            yield return new NodeMenuItemDefinition("Suggest", "Suggest",
                RequiredPermission: Permission.Update, DisplayOrder: 11);
    })
    .AddLayout(layout => layout
        .WithView("Suggest", MyEditArea.Suggest))
```

Items from `AddNodeMenuItems()` are merged with the defaults and sorted by `DisplayOrder`.

### NodeMenuItemDefinition

| Parameter | Type | Description |
|-----------|------|-------------|
| `Label` | `string` | Display text shown in the menu |
| `Area` | `string` | Layout area to navigate to when clicked |
| `Icon` | `string?` | Optional emoji or SVG URL; `null` to skip |
| `RequiredPermission` | `Permission` | Permission the user must have (e.g., `Permission.Update`) |
| `DisplayOrder` | `int` | Sort order within the menu (lower = earlier) |

### NodeMenuItemProvider

For advanced scenarios, register a provider delegate that yields items conditionally:

```csharp
config.AddNodeMenuItems(
    new NodeMenuItemProvider(async (host, ctx) =>
    {
        var canDoSpecialThing = await CheckSomethingAsync(host.Hub);
        if (canDoSpecialThing)
            yield return new NodeMenuItemDefinition("Special", "SpecialArea", DisplayOrder: 20);
    }))
```

## Generic Navigation

All menu items use generic navigation — clicking any item navigates to its declared `Area`. The area handler on the node hub is responsible for what happens (rendering a form, showing a dialog via `$Dialog` stream, etc.). There is no special-casing in the portal.

## MenuControl

`MenuControl` is stored at `$Menu` in the entity store (same pattern as `DialogControl` at `$Dialog`). It wraps an `IReadOnlyList<NodeMenuItemDefinition>`.

The `LayoutAreaView` component monitors the `$Menu` slot and publishes items to `IMenuItemsProvider`, which `PortalLayoutBase` subscribes to for rendering.

## Built-in Registrations

**All nodes** (via `AddDefaultMeshMenu`):
- Create, Import, Edit, Threads, Settings, Delete

**Markdown** nodes additionally register:
- **Suggest** (area: `Suggest`, permission: `Update`, order: 11) — editor with track changes

## See Also

- [DataBinding](MeshWeaver/Documentation/GUI/DataBinding) - How data flows through controls
- [Editor](MeshWeaver/Documentation/GUI/Editor) - The editor control for form rendering

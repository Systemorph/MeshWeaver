---
Name: Node Menu Items
Category: Documentation
Description: How node types register custom menu items in the portal's context menu
---

# Node Menu Items

The portal's node context menu (cube icon) is fully data-driven. Menu items are registered in the node's `HubConfiguration` via `IAsyncEnumerable` providers and rendered during the layout pipeline. A predicate-based renderer evaluates all providers and stores results at `$Menu` in the entity store (same pattern as `$Dialog`). The portal reads `$Menu` from the layout stream — no separate RPC needed.

## Default Menu Items

`AddDefaultMeshMenu()` (called automatically by `AddDefaultLayoutAreas()`) registers these items for all node types:

| Item | Area | Permission | Order | Notes |
|------|------|------------|-------|-------|
| Create | `Create` | `Create` | 0 | |
| Import | `ImportMeshNodes` | `Create` | 1 | |
| *node.Name* | *node.NodeType* | None | 10 | Navigates to NodeType definition via `Href` |
| Threads | `Threads` | None | 50 | |
| Settings | `Settings` | None | 90 | |
| Delete | `Delete` | `Delete` | 100 | |

The node-name item only appears when the MeshNode has a `NodeType` set. It uses `Href` for absolute navigation to the NodeType definition node instead of appending the area to the current path.

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
   |                                        |    → sorted by Order
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
                RequiredPermission: Permission.Update, Order: 11);
    })
    .AddLayout(layout => layout
        .WithView("Suggest", MyEditArea.Suggest))
```

Items from `AddNodeMenuItems()` are merged with the defaults and sorted by `Order`.

### NodeMenuItemDefinition

| Parameter | Type | Description |
|-----------|------|-------------|
| `Label` | `string` | Display text shown in the menu |
| `Area` | `string` | Layout area to navigate to when clicked |
| `Icon` | `string?` | Optional emoji or SVG URL; `null` to skip |
| `RequiredPermission` | `Permission` | Permission the user must have (e.g., `Permission.Update`) |
| `Order` | `int` | Sort order within the menu (lower = earlier) |
| `Href` | `string?` | Optional absolute href — when set, navigates directly instead of using Area |

### NodeMenuItemProvider

For advanced scenarios, register a provider delegate that yields items conditionally:

```csharp
config.AddNodeMenuItems(
    new NodeMenuItemProvider(async (host, ctx) =>
    {
        var canDoSpecialThing = await CheckSomethingAsync(host.Hub);
        if (canDoSpecialThing)
            yield return new NodeMenuItemDefinition("Special", "SpecialArea", Order: 20);
    }))
```

## Generic Navigation

Menu items navigate to their declared `Area` by appending it to the current path (e.g., `/TestOrg/Project/Settings`). When `Href` is set, the portal navigates to that absolute URL instead — used for cross-node navigation like the node-name → NodeType link.

## MenuControl

`MenuControl` is stored at `$Menu` in the entity store (same pattern as `DialogControl` at `$Dialog`). It wraps an `IReadOnlyList<NodeMenuItemDefinition>`.

The `LayoutAreaView` component monitors the `$Menu` slot and publishes items to `IMenuItemsProvider`, which `PortalLayoutBase` subscribes to for rendering.

## Built-in Registrations

**All nodes** (via `AddDefaultMeshMenu`):
- Create, Import, *node.Name* → NodeType, Threads, Settings, Delete

**Markdown** nodes additionally register:
- **Suggest** (area: `Suggest`, permission: `Update`, order: 11) — editor with track changes

## See Also

- [DataBinding](MeshWeaver/Documentation/GUI/DataBinding) - How data flows through controls
- [Editor](MeshWeaver/Documentation/GUI/Editor) - The editor control for form rendering

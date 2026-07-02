---
Name: Navigation Menus
Category: Documentation
Description: Build side navigation with NavMenu, NavGroup, and NavLink — collapsible groups, icons, and URL-based navigation
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h16"/><path d="M4 18h16"/></svg>
---

# Navigation Menus

Three controls compose every navigation menu in MeshWeaver:

| Control | Role |
|---|---|
| `NavMenuControl` (`Controls.NavMenu`) | The menu container — width, collapsibility |
| `NavGroupControl` (`Controls.NavGroup`) | A collapsible heading grouping related links |
| `NavLinkControl` (`Controls.NavLink`) | A clickable link with title, optional icon, and URL |

The framework's settings pages and node-type shells build their side menus from exactly these —
when a layout area needs navigation, declare them; never hand-roll `<a>` lists.

---

# Basic Menu

`WithNavLink(title, href)` appends a link; `WithNavLink(title, href, icon)` adds an icon from the
`FluentIcons` catalog. The URLs below are ordinary mesh paths — clicking navigates the portal.

```csharp --render NavMenuBasic --show-code
Controls.NavMenu
    .WithNavLink("GUI Overview", "/Doc/GUI", FluentIcons.Home())
    .WithNavLink("Data Grid", "/Doc/GUI/DataGrid", FluentIcons.Table())
    .WithNavLink("Layout Grid", "/Doc/GUI/LayoutGrid", FluentIcons.Grid())
```

---

# Grouped Menu

`WithNavGroup(title, config)` creates a collapsible section. Groups nest and take their own icon
and URL via `WithIcon` / `WithUrl`.

```csharp --render NavMenuGrouped --show-code
Controls.NavMenu
    .WithSkin(s => s.WithWidth(280))
    .WithNavGroup("Containers", g => g
        .WithIcon(FluentIcons.Folder())
        .WithNavLink("Stack", "/Doc/GUI/ContainerControl/Stack")
        .WithNavLink("Tabs", "/Doc/GUI/ContainerControl/Tabs")
        .WithNavLink("Splitter", "/Doc/GUI/ContainerControl/Splitter"))
    .WithNavGroup("Data", g => g
        .WithIcon(FluentIcons.Table())
        .WithNavLink("Data Grid", "/Doc/GUI/DataGrid")
        .WithNavLink("Data Binding", "/Doc/GUI/DataBinding"))
    .WithNavLink("Attributes", "/Doc/GUI/Attributes", FluentIcons.Settings())
```

---

# Configuration Reference

## NavMenu (skin)

| Method | Purpose |
|---|---|
| `WithSkin(s => s.WithWidth(int))` | Menu width in pixels |
| `WithSkin(s => s.WithCollapsible(bool))` | Allow collapsing to icon rail |
| `Collapse()` | Start collapsed |

## NavGroup

| Method | Purpose |
|---|---|
| `WithNavLink(title, href[, icon])` | Append a link to the group |
| `WithGroup(navGroup)` | Nest another group |
| `WithIcon(icon)` | Icon on the group heading |
| `WithUrl(url)` | Make the heading itself navigate |

---

# See Also

- [Side Panel](../SidePanel) — where contextual navigation often lives
- [Node Menu](../NodeMenu) — per-node context menus (a different mechanism)
- [Badges, Icons & Status](../DisplayControls) — the `FluentIcons` catalog used above

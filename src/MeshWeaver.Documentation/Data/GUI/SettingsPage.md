---
Name: The Node Settings Page
Category: Documentation
Description: The unified Settings page — a collapsible, independently-scrolling split view whose tabs are contributed by providers and searchable by name AND by the fields inside each section.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>
---

Every node has a **Settings** layout area at `/{nodePath}/Settings`. It renders a two-pane [Splitter](/Doc/GUI/ContainerControl/Splitter): a navigation menu on the left and the selected tab's content on the right. The tab is selected by the area id — `/{nodePath}/Settings/{tabId}` — so each tab is directly linkable.

The page is assembled from **contributed tabs**, not hard-coded. Any node type (or feature) registers tabs through a provider, and the page evaluates them reactively, filters by permission, and renders them sorted by order.

---

# Anatomy

```
┌─ Settings ─────────────────────────────────────────────┐
│  ┌───────────────┐ ║ ┌─────────────────────────────┐   │
│  │ 🔎 Search…     │ ║ │                             │   │
│  ├───────────────┤ ║ │   selected tab content      │   │
│  │ Metadata      │ ║ │   (scrolls independently)   │   │
│  │ ▸ Management  │ ║ │                             │   │
│  │ ▸ Security    │ ║ │                             │   │
│  │   Appearance  │ ║ │                             │   │
│  └───────────────┘ ║ └─────────────────────────────┘   │
│   menu pane         ↑ draggable / collapsible divider   │
└─────────────────────────────────────────────────────────┘
```

- **Two panes scroll independently.** The menu and the content each have their own scroll context; a long content tab never pushes the menu off-screen, and vice-versa.
- **The divider is draggable and the menu pane collapses** (it is marked `Collapsible`), so the content can take the full width when the menu is not needed.
- **A search box is pinned at the top of the menu pane** and filters the tabs live as you type.

> **Scrolling note.** The panes scroll via a flex chain (`flex: 1 1 auto; min-height: 0`), **not** `height: 100%`. A splitter pane's height comes from flex stretch, which is *indefinite* for percentage resolution — so a child `height: 100%` collapses to content height and is then clipped by the pane's `overflow: hidden`, leaving nothing to scroll. See [Splitter → Scrolling Panes](/Doc/GUI/ContainerControl/Splitter).

---

# Searching settings

The search box matches a query, case-insensitively, against three things per tab:

1. the tab **Label** (e.g. *"Appearance"*),
2. the tab **Group** (e.g. *"Security"*), and
3. the tab **Keywords** — terms describing the *fields inside* the section.

Keywords are what make the box search **content**, not just section names. Typing `dark mode`, `roles`, `icon`, or `version` surfaces the section that contains that setting even when the word never appears in the tab's title. Only the menu list re-renders as you type — the search box itself is a separate, static area, so it keeps focus.

The default tabs ship with keywords:

| Tab | A few of its keywords |
|---|---|
| Metadata | name, description, category, icon, order, namespace, version, timestamps |
| Node Types | types, definitions, schema, data model |
| Files | documents, uploads, attachments, collections |
| Access Control | permissions, roles, assignments, sharing, grant, deny |
| Groups | members, membership, teams |
| Effective Access | permissions, test, who can, audit |
| Appearance | theme, color, dark mode, light mode, style |

---

# Registering a tab

Contribute a tab from a node type's hub configuration with `AddSettingsMenuItems`. Pass `Keywords` so the section is reachable by the fields it contains, not just its label.

```csharp
config.AddSettingsMenuItems(
    new SettingsMenuItemDefinition(
        Id: "Notifications",                       // → /{node}/Settings/Notifications
        Label: "Notifications",
        ContentBuilder: BuildNotificationsTab,     // (host, stack, node) => UiControl
        Group: "Communication",                    // optional NavGroup heading
        Icon: FluentIcons.Alert(),
        Order: 250,
        RequiredPermission: Permission.Update,     // hidden unless the viewer has it
        Keywords: ["email", "digest", "frequency", "mute", "channels"]));
```

The `ContentBuilder` receives the `LayoutAreaHost`, a pre-classed content `StackControl` (already wired for internal scrolling), and the node, and returns the tab's content. Build it from the standard controls and the framework editors — see [Layout Areas](/Doc/GUI/LayoutAreas).

A provider may also yield tabs **reactively** (e.g. an admin-only tab that appears once a live permission check resolves) by registering a `SettingsMenuItemProvider` that returns `IObservable<IReadOnlyList<SettingsMenuItemDefinition>>`.

---

# SettingsMenuItemDefinition reference

| Parameter | Purpose |
|---|---|
| `Id` | Tab identifier; the area id in `/{node}/Settings/{Id}`. |
| `Label` | Display text in the menu (matched by search). |
| `ContentBuilder` | `(host, stack, node) => UiControl` — builds the tab content. |
| `Group` | Optional `NavGroup` heading (matched by search). |
| `Icon` / `GroupIcon` | Menu icon / group-header icon. |
| `Order` | Sort order; groups order by their minimum item order. |
| `RequiredPermission` | Tab is hidden unless the viewer holds this permission. |
| `Keywords` | Extra search terms for the **fields inside** the tab. Null = match by label/group only. |

---

# See Also

- [Splitter](/Doc/GUI/ContainerControl/Splitter) — the resizable/collapsible two-pane container, and the scrolling-panes pattern
- [Layout Areas](/Doc/GUI/LayoutAreas) — building the content a tab renders
- [Node Menu Items](/Doc/GUI/NodeMenu) — the sibling pattern for context-menu contributions
- [Data Model View](/Doc/GUI/DataModelView) — the Mermaid + JSON view used on the Node Types tab and a node type's `$Model` area

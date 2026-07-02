---
Name: Badges, Icons & Status
Category: Documentation
Description: Small display controls — badges, icons, progress bars, spacers, menu items, and card skins — each rendered live.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="8" r="6"/><path d="M15.5 13 17 22l-5-3-5 3 1.5-9"/></svg>
---

# Badges, Icons & Status

Beyond text and buttons, the framework ships a set of small display controls for status, decoration,
and spacing. Each is a typed control — never fall back to hand-built HTML for these.

---

# Badge

`BadgeControl` renders a small status pill. `Appearance` accepts the Fluent values `"Accent"`,
`"Neutral"`, and `"Lightweight"`; custom colors go through `BackgroundColor` + `Color`.

```csharp --render DisplayBadges --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("8px")
    .WithView(Controls.Badge("Released").WithAppearance("Accent"))
    .WithView(Controls.Badge("Draft").WithAppearance("Neutral"))
    .WithView(Controls.Badge("Deprecated")
        .WithBackgroundColor("var(--error)")
        .WithColor("white"))
```

---

# Icon

`IconControl` renders any icon from the `FluentIcons` catalog (in `MeshWeaver.Application.Styles`).
Icons theme with `currentColor`; pass an explicit size with `IconSize` from `MeshWeaver.Domain`
(e.g. `FluentIcons.Star(IconSize.Size16)`), or take the 24px default.

```csharp --render DisplayIcons --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("12px")
    .WithView(Controls.Icon(FluentIcons.CheckmarkCircle())
        .WithStyle("color: var(--success, green);"))
    .WithView(Controls.Icon(FluentIcons.Warning())
        .WithStyle("color: var(--warning, orange);"))
    .WithView(Controls.Icon(FluentIcons.Info())
        .WithStyle("color: var(--accent-fill-rest);"))
    .WithView(Controls.Icon(FluentIcons.Star()))
```

---

# Progress

`ProgressControl` shows a message with a percentage bar — the standard way to surface long-running
work (imports, compiles, exports) in a layout area. In real use the percentage comes from an
observable, so the bar advances as the operation reports progress.

```csharp --render DisplayProgress --show-code
Controls.Stack
    .WithView(Controls.Progress("Importing nodes…", 65))
    .WithView(Controls.Progress("Building search index…", 30))
```

---

# Spacer

`SpacerControl` fills the remaining space in a horizontal stack — the idiomatic way to push content
to opposite edges without CSS.

```csharp --render DisplaySpacer --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithWidth("100%")
    .WithView(Controls.Label("Quarterly Report 2026"))
    .WithView(Controls.Spacer)
    .WithView(Controls.Badge("Final").WithAppearance("Accent"))
```

---

# Menu Item

`MenuItemControl` is a titled, icon-carrying dropdown trigger. On its own it renders the trigger
button; add child views for the dropdown content. It backs the node menus described in
[Node Menu](../NodeMenu).

```csharp --render DisplayMenuItem --show-code
Controls.MenuItem("Export", FluentIcons.ArrowDownload())
    .WithView(Controls.Label("As PDF"))
    .WithView(Controls.Label("As Word"))
```

---

# Card Skin

Any control becomes a card by attaching `Skins.Card` — a bordered, elevated surface. Skins compose:
the control keeps its own behavior, the skin only wraps its presentation.

```csharp --render DisplayCard --show-code
Controls.Stack
    .WithView(Controls.H4("Funding Ratio"))
    .WithView(Controls.Markdown("**112.4%** — up from 109.8% last year"))
    .WithView(Controls.Badge("BVV2 Art. 44").WithAppearance("Neutral"))
    .AddSkin(Skins.Card.WithWidth("320px"))
```

---

# See Also

- [Container Controls](../ContainerControl) — stacks, tabs, toolbars, splitters
- [Node Menu](../NodeMenu) — where menu items appear on real nodes
- [Form Input Controls](../InputControls) — the interactive counterparts

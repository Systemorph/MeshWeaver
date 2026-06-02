---
Name: Adapting to Different Screens With Layout Grid
Category: Documentation
Description: Build responsive layouts that adapt gracefully to any screen size using a 12-column breakpoint system
Icon: /static/DocContent/GUI/LayoutGrid/icon.svg
---

`LayoutGrid` gives you a responsive layout system built on a **12-column grid**. Define how many columns each item spans at each screen size, and the layout shifts automatically — no media queries, no CSS wrestling.

---

# Why Responsive Design Matters

Every application reaches users across a wide range of devices. A single layout rarely serves them all well:

| Device | Screen Width | User Context |
|--------|--------------|--------------|
| **Smartphone** | < 600 px | On the go, touch input, limited space |
| **Tablet** | 600 – 1024 px | Mixed use, touch or keyboard, moderate space |
| **Desktop** | > 1024 px | Focused work, mouse and keyboard, plenty of space |

Responsive design lets you write **one layout** that adapts to all of them.

---

<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif">
<defs>
<marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
</marker>
</defs>
<rect width="760" height="310" rx="12" fill="#0d1117" opacity="0"/>
<text x="380" y="26" text-anchor="middle" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".75">Breakpoint Cascade — 12-Column Grid</text>
<rect x="10" y="40" width="130" height="56" rx="8" fill="#1e88e5"/>
<text x="75" y="63" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff">xs</text>
<text x="75" y="79" text-anchor="middle" font-size="10" fill="#cfe8ff">&lt; 600 px</text>
<rect x="160" y="40" width="130" height="56" rx="8" fill="#43a047"/>
<text x="225" y="63" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff">sm</text>
<text x="225" y="79" text-anchor="middle" font-size="10" fill="#c8e6c9">≥ 600 px</text>
<rect x="310" y="40" width="130" height="56" rx="8" fill="#f57c00"/>
<text x="375" y="63" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff">md</text>
<text x="375" y="79" text-anchor="middle" font-size="10" fill="#ffe0b2">≥ 960 px</text>
<rect x="460" y="40" width="130" height="56" rx="8" fill="#8e24aa"/>
<text x="525" y="63" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff">lg</text>
<text x="525" y="79" text-anchor="middle" font-size="10" fill="#e1bee7">≥ 1280 px</text>
<rect x="610" y="40" width="130" height="56" rx="8" fill="#5c6bc0"/>
<text x="675" y="63" text-anchor="middle" font-size="11" font-weight="bold" fill="#fff">xl</text>
<text x="675" y="79" text-anchor="middle" font-size="10" fill="#c5cae9">≥ 1920 px</text>
<line x1="140" y1="68" x2="158" y2="68" stroke="#90a4ae" stroke-opacity=".6" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="290" y1="68" x2="308" y2="68" stroke="#90a4ae" stroke-opacity=".6" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="440" y1="68" x2="458" y2="68" stroke="#90a4ae" stroke-opacity=".6" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="590" y1="68" x2="608" y2="68" stroke="#90a4ae" stroke-opacity=".6" stroke-width="1.5" marker-end="url(#arr)"/>
<text x="380" y="120" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".55">settings cascade upward — unset breakpoints inherit from the next smaller one</text>
<text x="10" y="150" font-size="11" fill="currentColor" fill-opacity=".6">Example: .WithXs(12) .WithSm(6) .WithLg(3)</text>
<rect x="10" y="162" width="110" height="38" rx="6" fill="#1e88e5" opacity=".9"/>
<text x="65" y="186" text-anchor="middle" font-size="11" fill="#fff">xs: 12 cols</text>
<rect x="130" y="162" width="110" height="38" rx="6" fill="#43a047" opacity=".9"/>
<text x="185" y="186" text-anchor="middle" font-size="11" fill="#fff">sm: 6 cols</text>
<rect x="250" y="162" width="110" height="38" rx="6" fill="#f57c00" opacity=".9"/>
<text x="305" y="186" text-anchor="middle" font-size="11" fill="#fff">md: 6 cols ↑</text>
<rect x="370" y="162" width="110" height="38" rx="6" fill="#8e24aa" opacity=".9"/>
<text x="425" y="186" text-anchor="middle" font-size="11" fill="#fff">lg: 3 cols</text>
<rect x="490" y="162" width="110" height="38" rx="6" fill="#5c6bc0" opacity=".9"/>
<text x="545" y="186" text-anchor="middle" font-size="11" fill="#fff">xl: 3 cols ↑</text>
<text x="665" y="182" font-size="10" fill="currentColor" fill-opacity=".5">↑ inherited</text>
<text x="10" y="225" font-size="11" fill="currentColor" fill-opacity=".6">12-column row — items wrap when column total exceeds 12</text>
<rect x="10" y="238" width="360" height="34" rx="6" fill="#1565c0" opacity=".85"/>
<text x="190" y="259" text-anchor="middle" font-size="11" fill="#fff">Item A — 6 cols</text>
<rect x="375" y="238" width="360" height="34" rx="6" fill="#1565c0" opacity=".65"/>
<text x="555" y="259" text-anchor="middle" font-size="11" fill="#fff">Item B — 6 cols</text>
<rect x="10" y="278" width="117" height="26" rx="6" fill="#1976d2" opacity=".85"/>
<text x="68" y="295" text-anchor="middle" font-size="10" fill="#fff">4 cols</text>
<rect x="132" y="278" width="117" height="26" rx="6" fill="#1976d2" opacity=".85"/>
<text x="190" y="295" text-anchor="middle" font-size="10" fill="#fff">4 cols</text>
<rect x="254" y="278" width="117" height="26" rx="6" fill="#1976d2" opacity=".85"/>
<text x="312" y="295" text-anchor="middle" font-size="10" fill="#fff">4 cols</text>
<line x1="375" y1="285" x2="735" y2="285" stroke="currentColor" stroke-opacity=".18" stroke-width="1" stroke-dasharray="4,3"/>
<text x="555" y="296" text-anchor="middle" font-size="10" fill="currentColor" fill-opacity=".4">(next row begins here)</text>
</svg>

*Breakpoints cascade upward through xs → sm → md → lg → xl; unset breakpoints inherit the nearest smaller value.*

---

# The 12-Column System

`LayoutGrid` divides the available width into **12 equal columns**. Each item declares how many columns it occupies. The columns always add up to 12 per row; items that overflow wrap to the next row automatically.

| Columns | Proportional Width | Typical Use |
|---------|--------------------|-------------|
| 12 | 100 % | Full-width content, mobile layouts |
| 6 | 50 % | Two equal side-by-side columns |
| 4 | 33 % | Three equal columns |
| 3 | 25 % | Four-column grids, narrow sidebars |

```csharp --render ColumnDemo --show-code
Controls.LayoutGrid
    .WithSkin(skin => skin.WithSpacing(2))
    .WithView(Controls.Html("<div style='background:#1565c0;color:white;padding:12px;text-align:center;border-radius:4px'>12 cols (100%)</div>"), s => s.WithXs(12))
    .WithView(Controls.Html("<div style='background:#1976d2;color:white;padding:12px;text-align:center;border-radius:4px'>6 cols</div>"), s => s.WithXs(6))
    .WithView(Controls.Html("<div style='background:#1976d2;color:white;padding:12px;text-align:center;border-radius:4px'>6 cols</div>"), s => s.WithXs(6))
    .WithView(Controls.Html("<div style='background:#2196f3;color:white;padding:12px;text-align:center;border-radius:4px'>4</div>"), s => s.WithXs(4))
    .WithView(Controls.Html("<div style='background:#2196f3;color:white;padding:12px;text-align:center;border-radius:4px'>4</div>"), s => s.WithXs(4))
    .WithView(Controls.Html("<div style='background:#2196f3;color:white;padding:12px;text-align:center;border-radius:4px'>4</div>"), s => s.WithXs(4))
```

---

# Breakpoints: Adapting to Screen Size

A **breakpoint** is a screen-width threshold where the layout can change. `LayoutGrid` provides five, matching standard Material Design conventions:

| Method | Activates at | Screen type | Example devices |
|--------|-------------|-------------|-----------------|
| `.WithXs()` | < 600 px | Extra small | iPhones, Android phones |
| `.WithSm()` | ≥ 600 px | Small | Large phones, small tablets |
| `.WithMd()` | ≥ 960 px | Medium | iPads, tablets |
| `.WithLg()` | ≥ 1280 px | Large | Laptops, small monitors |
| `.WithXl()` | ≥ 1920 px | Extra large | Large monitors, 4 K displays |

> **Cascading upward.** Settings inherit toward larger breakpoints. Setting `.WithMd(4)` applies to medium screens *and every larger size* unless you explicitly override it with `.WithLg()` or `.WithXl()`.

---

# Designing for Small Screens (Smartphones)

Mobile screens are narrow. The guiding principle is **stack first**: make every item full-width on extra-small screens with `WithXs(12)`, then introduce side-by-side columns as the screen grows.

Key guidelines:
- **Stack vertically** — `WithXs(12)` ensures nothing squeezes awkwardly.
- **Prioritize content** — the most important information should appear at the top.
- **Touch-friendly targets** — buttons and links need enough tap area.

```csharp --render MobileFirst --show-code
Controls.LayoutGrid
    .WithSkin(skin => skin.WithSpacing(2))
    .WithView(Controls.Html("<div style='background:#2e7d32;color:white;padding:16px;border-radius:4px'><b>Primary Content</b><br/>Full width on mobile</div>"), s => s
        .WithXs(12)    // Full width on phones
        .WithMd(8))    // 2/3 width on tablets+
    .WithView(Controls.Html("<div style='background:#388e3c;color:white;padding:16px;border-radius:4px'><b>Sidebar</b><br/>Below on mobile, side on tablet</div>"), s => s
        .WithXs(12)    // Full width on phones (stacks below)
        .WithMd(4))    // 1/3 width on tablets+ (appears beside)
```

---

# Designing for Medium Screens (Tablets)

Tablets offer enough room for two-column layouts and are often used in both portrait and landscape orientations. A `3 + 9` or `4 + 8` split works well for navigation-plus-content patterns.

Key guidelines:
- **Two-column layouts** — ideal for master-detail or nav-plus-content views.
- **Flexible splits** — `6 + 6`, `4 + 8`, `3 + 9` all fit naturally.
- **Consider orientation** — portrait is meaningfully narrower than landscape.

```csharp --render TabletLayout --show-code
Controls.LayoutGrid
    .WithSkin(skin => skin.WithSpacing(3))
    .WithView(Controls.Html("<div style='background:#1565c0;color:white;padding:20px;border-radius:4px;min-height:80px'><b>Navigation</b></div>"), s => s
        .WithXs(12)    // Full width on phone
        .WithMd(3))    // Narrow sidebar on tablet
    .WithView(Controls.Html("<div style='background:#1976d2;color:white;padding:20px;border-radius:4px;min-height:80px'><b>Main Content Area</b><br/>Most of the screen on tablet</div>"), s => s
        .WithXs(12)    // Full width on phone
        .WithMd(9))    // Wide main area on tablet
```

---

# Designing for Large Screens (Desktops)

Desktop users have space to spare — use it to show more information at once without overwhelming them. Card grids and multi-column panels shine here, but be mindful that very wide text lines are harder to read.

Key guidelines:
- **Multi-column layouts** — show collections in 3 or 4 column grids.
- **Card grids** — a classic `1 → 2 → 4` progression scales cleanly across sizes.
- **Don't overstretch** — constrain maximum reading width for text-heavy sections.

```csharp --render DesktopLayout --show-code
Controls.LayoutGrid
    .WithSkin(skin => skin.WithSpacing(2))
    .WithView(Controls.Html("<div style='background:#7b1fa2;color:white;padding:16px;border-radius:4px'>Card 1</div>"), s => s
        .WithXs(12).WithSm(6).WithLg(3))  // 1 col -> 2 cols -> 4 cols
    .WithView(Controls.Html("<div style='background:#7b1fa2;color:white;padding:16px;border-radius:4px'>Card 2</div>"), s => s
        .WithXs(12).WithSm(6).WithLg(3))
    .WithView(Controls.Html("<div style='background:#7b1fa2;color:white;padding:16px;border-radius:4px'>Card 3</div>"), s => s
        .WithXs(12).WithSm(6).WithLg(3))
    .WithView(Controls.Html("<div style='background:#7b1fa2;color:white;padding:16px;border-radius:4px'>Card 4</div>"), s => s
        .WithXs(12).WithSm(6).WithLg(3))
```

---

# Common Responsive Patterns

These three patterns cover the majority of real-world layouts.

## Dashboard Cards

Items flow from a single column on phones to a four-column grid on wide monitors:

```csharp
Controls.LayoutGrid
    .WithSkin(skin => skin.WithSpacing(3))
    .WithView(metricCard1, s => s.WithXs(12).WithSm(6).WithLg(3))  // 1 -> 2 -> 4 columns
    .WithView(metricCard2, s => s.WithXs(12).WithSm(6).WithLg(3))
    .WithView(metricCard3, s => s.WithXs(12).WithSm(6).WithLg(3))
    .WithView(metricCard4, s => s.WithXs(12).WithSm(6).WithLg(3))
```

## Sidebar + Content

A narrow navigation sidebar that stacks below the content on mobile:

```csharp
Controls.LayoutGrid
    .WithView(sidebar, s => s.WithXs(12).WithMd(3))   // Full width then sidebar
    .WithView(content, s => s.WithXs(12).WithMd(9))   // Full width then main
```

## Equal Thirds

Three equal columns that stack vertically on mobile and spread out on tablet and above:

```csharp
Controls.LayoutGrid
    .WithView(col1, s => s.WithXs(12).WithMd(4))  // Stack on mobile, thirds on tablet+
    .WithView(col2, s => s.WithXs(12).WithMd(4))
    .WithView(col3, s => s.WithXs(12).WithMd(4))
```

---

# Grid Configuration

Control the container itself via `.WithSkin()`:

| Method | Purpose | Values |
|--------|---------|--------|
| `.WithSpacing(n)` | Gap between items | `1`, `2`, `3` (multiples of the base spacing unit) |
| `.WithJustify(j)` | Horizontal alignment of the row | `"start"`, `"center"`, `"end"` |

---

# Item Configuration

Each item added with `.WithView(content, skin => ...)` accepts these sizing methods:

| Method | Screen size | Activates at |
|--------|-------------|-------------|
| `.WithXs(cols)` | Extra small | < 600 px |
| `.WithSm(cols)` | Small | ≥ 600 px |
| `.WithMd(cols)` | Medium | ≥ 960 px |
| `.WithLg(cols)` | Large | ≥ 1280 px |
| `.WithXl(cols)` | Extra large | ≥ 1920 px |

`cols` is an integer from 1 to 12. Items with no explicit size for a breakpoint inherit from the next smaller breakpoint that is set.

---

# See Also

- [Container Control](../ContainerControl) — Stack, Tabs, Toolbar
- [DataGrid](../DataGrid) — Tabular data display

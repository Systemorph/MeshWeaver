---
Name: Adapting to Different Screens With Layout Grid
Category: Documentation
Description: Build responsive layouts that adapt beautifully to any screen size
Icon: /static/DocContent/GUI/LayoutGrid/icon.svg
---

The LayoutGrid control creates responsive layouts that automatically adapt to different screen sizes - from smartphones to large desktop monitors.

---

# Why Responsive Design Matters

Users access your application on many different devices:

| Device | Screen Width | User Context |
|--------|--------------|--------------|
| **Smartphone** | < 600px | On the go, touch input, limited space |
| **Tablet** | 600px - 1024px | Mixed use, touch or keyboard, moderate space |
| **Desktop** | > 1024px | Focused work, mouse/keyboard, plenty of space |

A responsive layout ensures your UI looks good and works well on all of them - without writing separate code for each device.

---

# The 12-Column System

LayoutGrid divides the screen into **12 equal columns**. Each item specifies how many columns it spans:

| Columns | Width | Use for |
|---------|-------|---------|
| 12 | 100% | Full-width content, mobile layouts |
| 6 | 50% | Two equal columns |
| 4 | 33% | Three equal columns |
| 3 | 25% | Four equal columns, sidebars |

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

The key to responsive design is **breakpoints** - screen widths where your layout changes. LayoutGrid provides five breakpoints:

| Method | Breakpoint | Screen Type | Example Devices |
|--------|------------|-------------|-----------------|
| `.WithXs()` | < 600px | Extra small | iPhone, Android phones |
| `.WithSm()` | >= 600px | Small | Large phones, small tablets |
| `.WithMd()` | >= 960px | Medium | iPad, tablets |
| `.WithLg()` | >= 1280px | Large | Laptops, small monitors |
| `.WithXl()` | >= 1920px | Extra large | Large monitors, 4K displays |

**How it works:** Settings cascade upward. If you set `.WithMd(4)`, that applies to medium screens *and all larger screens* unless you override with `.WithLg()` or `.WithXl()`.

---

# Designing for Small Screens (Smartphones)

On smartphones, screen space is precious. Best practices:

- **Stack vertically**: Use `WithXs(12)` to make items full-width
- **Prioritize content**: Show the most important information first
- **Touch-friendly**: Ensure buttons and links are easy to tap

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

Tablets offer more space but are often used in both portrait and landscape orientations:

- **Two-column layouts**: Work well for master-detail views
- **Flexible widths**: Use 6+6 or 4+8 splits
- **Consider orientation**: Portrait is narrower than landscape

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

Desktop users have plenty of space - use it wisely:

- **Multi-column layouts**: Show more information side-by-side
- **Card grids**: Display collections in 3 or 4 column grids
- **Don't stretch too wide**: Very wide text is hard to read

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

## Dashboard Cards

```csharp
Controls.LayoutGrid
    .WithSkin(skin => skin.WithSpacing(3))
    .WithView(metricCard1, s => s.WithXs(12).WithSm(6).WithLg(3))  // 1 -> 2 -> 4 columns
    .WithView(metricCard2, s => s.WithXs(12).WithSm(6).WithLg(3))
    .WithView(metricCard3, s => s.WithXs(12).WithSm(6).WithLg(3))
    .WithView(metricCard4, s => s.WithXs(12).WithSm(6).WithLg(3))
```

## Sidebar + Content

```csharp
Controls.LayoutGrid
    .WithView(sidebar, s => s.WithXs(12).WithMd(3))   // Full width then sidebar
    .WithView(content, s => s.WithXs(12).WithMd(9))   // Full width then main
```

## Equal Thirds

```csharp
Controls.LayoutGrid
    .WithView(col1, s => s.WithXs(12).WithMd(4))  // Stack on mobile, thirds on tablet+
    .WithView(col2, s => s.WithXs(12).WithMd(4))
    .WithView(col3, s => s.WithXs(12).WithMd(4))
```

---

# Grid Configuration

Configure the grid container via `.WithSkin()`:

| Method | Purpose | Values |
|--------|---------|--------|
| `.WithSpacing(n)` | Gap between items | `1`, `2`, `3` (multiplied by base unit) |
| `.WithJustify(j)` | Horizontal alignment | `"start"`, `"center"`, `"end"` |

---

# Item Configuration

Configure individual items via the skin function parameter:

| Method | Purpose |
|--------|---------|
| `.WithXs(cols)` | Columns on extra small screens (< 600px) |
| `.WithSm(cols)` | Columns on small screens (>= 600px) |
| `.WithMd(cols)` | Columns on medium screens (>= 960px) |
| `.WithLg(cols)` | Columns on large screens (>= 1280px) |
| `.WithXl(cols)` | Columns on extra large screens (>= 1920px) |

---

# See Also

- [Container Control](../ContainerControl) - Stack, Tabs, Toolbar
- [DataGrid](../DataGrid) - Tabular data display


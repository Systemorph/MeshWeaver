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

## Inline SVG icons render as images

A node's `Icon` (and an `IconControl` value) may also hold inline `<svg>` markup. That value is
user-settable content shown to other viewers, and SVG can carry script and event handlers, so it
is **never injected into the page as markup**. Every renderer draws it as an `<img>` whose source
is the icon as a `data:image/svg+xml` URI — an image-loaded SVG runs no script, fires no handler
and loads nothing external:

- server-side raw HTML: `MeshNodeImageHelper.InlineSvgImage(svg, pixels)` (and `SizeInlineSvg`,
  which now returns the same inert image);
- a renderer building its own element: `<img src="@MeshNodeImageHelper.InlineSvgImageSource(svg)">`.

Both plate the icon first (`IconBackplate.Ensure`). One visual consequence: an image cannot inherit
the page's text colour, so `currentColor` left inside an icon that paints its own plate resolves to
black — pass `currentColor:` to `InlineSvgImage` / `InlineSvgImageSource` to give it a colour. That
colour becomes the document's inherited default (a `color` attribute on the root `<svg>`, set only
when the root declares none); paint is never rewritten, so colours the icon declares itself still
win. An icon without its own plate is unaffected, because plating already recolours `currentColor`
to white.

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

# QR Code, Share Link & Install

Three controls for a page a person carries on their phone — first used by the action-link phone
card (MeshWeaver.Plugins#2062). All three take a literal or a data-binding pointer for every value,
so a template emits them at once and the values arrive bound.

**`QrCodeControl`** draws a QR code from a module matrix the SERVER computed: `Controls.QrCodeOf(text)`
encodes on the spot, and `QrCodes.Encode(text)` returns the matrix (rows of `'1'`/`'0'`) for a
projection to bind (`Controls.QrCode(pointer)`). Every renderer — Blazor, React, React Native — only
paints squares, so none needs a QR library and no third-party service sees the text. It is always
black on white with the standard four-module quiet zone, in the dark theme too (cameras need the
contrast), and nothing is drawn over it. `Size` is the rendered width in CSS pixels (default 240),
`Label` the accessible name; `QrCodes.Encode` takes a `QrErrorCorrection` level (default `Medium`)
and refuses one it does not know by name. An empty matrix draws nothing, so clearing the bound value
removes the code. 🚨 Everything encoded is readable by any camera that sees the screen: encode a
public address, never a credential or a personal detail, and always show the same address as text
beside it.

**`ShareLinkControl`** is **Copy link** and **Share** for `Url`. Copy writes to the clipboard inside
the click (Safari refuses a write that has left the gesture). Share opens the device's share sheet
and is offered only where the browser has one; elsewhere Copy stands alone. The outcome is
announced in a polite live region. An empty `Url` disables both.

**`InstallAppControl`** is **Install on iPhone / Android** for the page it is on. It adds nothing by
itself. Opened, it guides iPhone and iPad through Share → *Add to Home Screen* (Safari has no
programmatic prompt). On Android it raises the browser's own install prompt when the browser offered
one, and shows the browser-menu steps when it did not. An in-app browser is told to open the page in
Safari or Chrome first, a page already opened from the home screen says so, and a desktop browser
shows the address to open on the phone. The detected platform can be switched by hand. While it is
rendered, the page's manifest is `ManifestHref`, so each page installs under its own identity and
scope. The previous manifest comes back when the control goes away.

```csharp
Controls.Stack
    .WithView(Controls.QrCode(new JsonPointerReference("qrModules")).WithLabel("Invitation"))  // QrCodes.Encode(url)
    .WithView(Controls.Body(new JsonPointerReference("url")))          // the address, readable
    .WithView(Controls.ShareLink(new JsonPointerReference("url")).WithTitle("Welcome"))
    .WithView(Controls.InstallApp("/a/0123/manifest.webmanifest").WithAppName("Course card"))
```

The labels and install steps are the platform's own (`qr.*`, `share.*`, `install.*` in the string
catalog), in the viewer's language.

---

# See Also

- [Container Controls](../ContainerControl) — stacks, tabs, toolbars, splitters
- [Node Menu](../NodeMenu) — where menu items appear on real nodes
- [Form Input Controls](../InputControls) — the interactive counterparts

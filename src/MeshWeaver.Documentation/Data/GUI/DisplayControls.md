---
Name: Badges, Icons & Status
Category: Documentation
Description: Small display controls — badges, icons, images, progress bars, spacers, dividers, embedded documents and apps, audio players, menu items, and card skins.
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

## A navigating chip

A compact, clickable status pill that opens a node is a badge with `Href`. Do not draw it as an
anchor in `Controls.Html`. `Tooltip` carries the full label, and `MaxWidth` cuts longer text with an
ellipsis. Colors take theme tokens (`var(--accent-fill-rest)`, `var(--error, #d13438)`), so the chip
follows light and dark themes.

```csharp --render DisplayBadgeChips --show-code
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("8px")
    .WithView(Controls.Badge("Badges, Icons & Status")
        .WithHref("/Doc/GUI/DisplayControls")
        .WithTooltip("Badges, Icons & Status")
        .WithMaxWidth("120px")
        .WithBackgroundColor("var(--accent-fill-rest)")
        .WithColor("var(--foreground-on-accent-rest)"))
```

The renderer applies the same address rules as `LinkControl`. A mesh path such as `/Doc/GUI`
navigates inside the portal. An `http(s)` address opens with `rel="noopener noreferrer"`. Any other
scheme draws the badge without a link. The text and the tooltip are free text that the renderer
encodes, so the caller never escapes them. The tooltip is user-facing, so pass a localized string,
for example from `host.Localize(...)`.

---

# Icon

`IconControl` renders any icon from the `FluentIcons` catalog (in `MeshWeaver.Application.Styles`).
Icons theme with `currentColor`; pass an explicit size with `IconSize` from `MeshWeaver.Domain`
(e.g. `FluentIcons.Star(IconSize.Size16)`), or take the 24px default. For a picture, an SVG, an
emoji or a node's icon, which can be any of those, use [Image](#image).

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

## The node-icon catalogue

> The catalogue's new glyphs are Microsoft **Fluent UI System Icons** (MIT). Their notice ships with them in
> `src/MeshWeaver.Graph/ThirdPartyNotices/fluentui-system-icons.txt`, embedded in the assembly, and lists each file it
> covers.

A node's `Icon` may be a Fluent icon name such as `"DocumentText"`, but not every surface can draw
every Fluent name. The Blazor navigation renders any name through FluentUI; a link preview, an
HTML card or a browser tab can only draw a name whose glyph the platform ships
(`MeshNodeImageHelper.ShippedIconFor` → `/static/NodeTypeIcons/{name}.svg`).

`NodeIconCatalog` (namespace `MeshWeaver.Graph`) is the fixed list of names that renders
everywhere: about 150 broadly useful Fluent UI System Icons names (documents, people and
organisations, finance, data and analytics, communication, time, development, security, places,
science, education, media, tools, insurance and risk, legal, health, shopping), each shipped as a
20 px tile in `MeshWeaver.Graph/Icons/`.

| Member | What it is |
|---|---|
| `NodeIconCatalog.Entries` | `IReadOnlyList<NodeIconCatalog.Entry>`, each with a `Name` (the PascalCase Fluent name written to `Icon`) and a `Meaning` (a short keyword hint) |
| `NodeIconCatalog.Contains(name)` | case-insensitive membership; null, empty and unknown names are not members |

**Automatic icon assignment picks only from this catalogue.** A model choosing an icon for a node is
given the `Name`/`Meaning` pairs and its answer is accepted only when `Contains` holds, so an
automatically chosen icon can never be a name that some surface fails to draw. An icon a person
sets by hand is not limited to the catalogue.

Adding a name means adding its `Icons/{lowercase name}.svg` in the same change, drawn as the house
tile (`viewBox="0 0 20 20"`, a full-bleed `rx="4"` rect in `#0078D4`, the official Fluent 20 px
Regular path in white). `NodeIconCatalogTest` fails on an entry without its glyph, a duplicate
name, or a glyph off the tile.

---

# Image

`ImageControl` draws one picture, glyph or icon in a sized, shaped box. Use it for a node icon,
a logo, a thumbnail, a poster or an avatar — never an `<img>`, an inline `<svg>` or an emoji
`<div>` inside `Controls.Html`. The source is any form a node's `Icon` holds:

| Source | Drawn as |
|---|---|
| An image URL, `/api/content/…`, or a `data:image/…` URI | `<img>` |
| A node's `content:logo.png` reference (with `WithNodePath`) | `<img>` of the access-controlled content URL |
| Raw `<svg>` markup | `<img src="data:image/svg+xml,…">`, never injected markup |
| A text glyph (an emoji) | text |
| A Fluent icon name (`"Document"`) or a typed `Icon` | the Fluent icon |

`Controls.Image(source, alt)` requires the alt text, and the caller localizes it. An empty `""`
marks the image decorative, for an image that sits beside a label that already names it.

```csharp
Controls.Stack
    .WithOrientation(Orientation.Horizontal)
    .WithHorizontalGap("12px")
    // A node's icon in a rounded tile, its type glyph as the fallback, decorative beside the title.
    .WithView(Controls.Image(new JsonPointerReference("icon"), "")
        .WithSize(48)
        .WithShape(ImageShape.Rounded)
        .WithNodePath(nodePath)
        .WithFallback("/static/NodeTypeIcons/box.svg"))
    // An avatar: the picture, cropped to a circle, or the person's initials.
    .WithView(Controls.Image(new JsonPointerReference("picture"), pictureAlt) // localized by the caller
        .WithShape(ImageShape.Circle)
        .WithFit(ImageFit.Cover)
        .WithInitials(ImageSources.InitialsOf(name) ?? ""))
    // A poster: not square, so width and height override the size.
    .WithView(Controls.Image(posterUrl, posterAlt)
        .WithWidth("100%").WithHeight(180).WithFit(ImageFit.Cover))
```

| Property | Default | Meaning |
|---|---|---|
| `Size` | `32` | Side of the square box in pixels, or a CSS length (`2.5rem`, `100%`) |
| `Width`, `Height` | — | Override `Size` for a box that is not square |
| `MaxWidth`, `MaxHeight` | — | Bound a picture and keep its natural aspect — see below |
| `Shape` | `ImageShape.Square` | `Square`, `Rounded` or `Circle` |
| `Fit` | `ImageFit.Contain` | `Contain` (icons, logos) or `Cover` (photos, posters) |
| `Fallback` | — | Drawn when the source is absent or refused; any source form |
| `Initials` | — | Monogram drawn when neither source nor fallback can be |
| `NodePath` | — | The node a `content:` reference belongs to |

Every property takes a literal or a data-binding pointer.

## Drawn safely

Every renderer resolves the control through `ImageSources.Resolve`, the one rule set:

- A URL is drawn only when `ImageSources.SafeUrl` accepts it: `http`, `https`, a relative path,
  or a `data:` URI of an image type. `javascript:`, `vbscript:`, `file:`, `data:text/html` and any
  value with a control character in it (`java⇥script:`) are refused. A refused source falls through to
  `Fallback`, then `Initials`, then nothing.
- SVG markup is drawn only as `<img src="ImageSources.SvgDataUri(…)">`. A browser runs no script,
  fires no event handler and loads nothing external in an image context, so a `<script>` or an
  `onload` inside the markup is inert. Renderers apply the icon backplate before encoding, as for
  every inline icon.
- A sized value is accepted only as a number or a number with a CSS unit (`ImageSources.CssLength`),
  so a bound value cannot add a second style declaration.
- A `content:` reference without a `NodePath` is not drawn. It is never shown as text.
- An unknown `Shape` or `Fit` gets no styling, and the renderer logs it. It is never mapped onto a
  known value.

## Natural-aspect bounds

A logo or a banner has no fixed box: a wide wordmark and a square avatar must both render whole.
Set `MaxWidth` and/or `MaxHeight` and a PICTURE (a URL or SVG source) keeps its natural aspect inside
those bounds; `Size` then no longer sizes it, while an explicit `Width` or `Height` still does. The
glyph, icon and initials forms have no natural size and keep using `Size`. A plate behind the logo is
plain style on the control:

```csharp
Controls.Image(space.Logo, "")
    .WithMaxWidth("340px")
    .WithMaxHeight("96px")
    .WithShape(ImageShape.Rounded)
    .WithInitials(ImageSources.InitialsOf(space.Name))
    .WithStyle("padding: 6px; background: var(--neutral-layer-2);")
```

---

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

# Divider

`Controls.Divider` is a thin rule in the theme's neutral stroke colour, with the `separator` role —
the control for a hand-built `<hr style=…>`. It is horizontal by default;
`WithOrientation(Orientation.Vertical)` draws a full-height rule between side-by-side content.
Spacing is the surrounding stack's gap, or a margin through `WithStyle`.

```csharp --render DisplayDivider --show-code
Controls.Stack
    .WithVerticalGap(16)
    .WithView(Controls.Label("Quarterly Report 2026"))
    .WithView(Controls.Divider)
    .WithView(Controls.Label("Revenue grew in every region."))
```

---

# Embed

`Controls.Embed(src, title)` frames a document or another site's page — the control for a
hand-built `<iframe>`. The title is required: it is the frame's accessible name, and the caller
localizes it. Every renderer loads the frame lazily, draws a theme-aware border, and applies
`EmbedSources`:

| `Kind` | Accepted `Src` | Sandbox |
|---|---|---|
| `EmbedKind.Document` (default) | a RELATIVE address of this portal (`/api/content/…`) | none — browsers refuse to draw a PDF in a sandboxed frame |
| `EmbedKind.App` | an `http`/`https` or scheme-relative `//host` address on ANOTHER host than the portal's | ALWAYS, with `Allow`'s tokens only; no referrer is sent |

A refused address draws the title as text and no frame. `Allow` takes tokens from `EmbedSandbox`
(`Scripts`, `SameOrigin`, `Forms`, `Popups`, `Downloads`; `Scripts` + `Forms` by default) and
nothing else — `allow-top-navigation` and `allow-popups-to-escape-sandbox` are never written. An app
on the portal's own host is refused outright however it is spelled (`https://portal…`, `//portal…`,
or `https:/app`, which a browser resolves against the page), because script plus same-origin would
let it lift its own sandbox. Renderers pass the origin they are served from to
`EmbedSources.SafeSrc`, and a render with no page origin refuses an app.

```csharp
// A PDF from a node's content collection, with a download link beside it.
Controls.Stack
    .WithView(Controls.Embed(contentUrl, fileName))
    .WithView(Controls.Link(downloadText, contentUrl).WithDownload(fileName))

// An external app. Many sites refuse to be framed and the browser shows a blank box without
// saying so — always offer the address in a new tab next to it.
Controls.Stack
    .WithView(Controls.Embed(appUrl, appTitle)
        .WithKind(EmbedKind.App)
        .WithHeight("78vh").WithMaxHeight("900px")
        .WithAllow(EmbedSandbox.Scripts, EmbedSandbox.SameOrigin, EmbedSandbox.Forms))
    .WithView(Controls.Link(openInNewTabText, appUrl).WithOpenInNewTab())
```

For a video use `Controls.Video`, which frames embed players itself.

---

# Audio

`Controls.Audio(src)` is an in-place audio player: the control for a hand-built `<audio controls>`
tag. It has no frame and no aspect ratio, so it fits a table cell or a stack row — a 16/9
`Controls.Video` is not a substitute. `Title` is the player's accessible name (`title` and
`aria-label`); the caller localizes it. `Preload` takes an `AudioPreload` value — `None`,
`Metadata` (the default) or `Auto` — and a list of clips wants `None`, so nothing downloads until
the viewer presses play.

```csharp
// One clip.
Controls.Audio(previewUrl).WithTitle(previewTitle).WithPreload(AudioPreload.None)

// A preview column in a grid: the source is bound to each row's (camelCase) property.
Controls.Audio(new ContextProperty("previewUrl")).WithPreload(AudioPreload.None)
```

`Src` is an ordinary bindable property, so it takes a data-binding expression as well as a URL.

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

## Embedded payment-method form

**`PaymentSetupFormControl`** (`Controls.PaymentSetupForm(setupId)`) renders the payment provider's
own in-page form for one open, client-rendered payment setup. For Stripe that is the Payment Element,
with the Express Checkout Element for Apple Pay and Google Pay where the device and account are
eligible. The person saves a method on the page they are on, and **nothing is charged**. It was first
used by the coupon journey's review page (MeshWeaver.Plugins#2064).

🚨 **The control carries no secret, by construction.** It names only the setup (`SetupId`) and what
is public by nature: `Provider` (which renderer draws it), `ClientPublicKey` (the publishable key),
`ReturnUrl` (where an off-page 3-D Secure step returns to) and `Note`. The provider's one-time client
secret is never a property of the control, so it cannot land in a layout area's data, a node or a
stream snapshot. The renderer runs server-side in the **buyer's own circuit**. It asks the site's
provider for the secret with that circuit's signed-in identity
(`IPaymentMethodSetupProvider.RevealClientSecret(setupId, buyer)`), and the provider answers only the
buyer the setup was opened for. The secret then goes straight to the provider's script in that one
browser. The form never decides whether a method was saved. The provider's verified delivery or
server-side read does that.

```csharp
Controls.PaymentSetupForm(new JsonPointerReference("setupId"))
    .WithProvider("Stripe")
    .WithClientPublicKey(new JsonPointerReference("publishableKey"))
    .WithReturnUrl(new JsonPointerReference("returnUrl"))
```

The labels are the platform's own (`paymentSetup.*` in the string catalog), in the viewer's language.
The renderers live with the portal hosts (MeshWeaver.Plugins). Loading a provider's script needs that
host's Content-Security-Policy to admit it (for Stripe, `https://js.stripe.com` in `script-src`; its
frames and API are already inside `frame-src https:` and `connect-src https:`).

---

# See Also

- [Container Controls](../ContainerControl) — stacks, tabs, toolbars, splitters
- [Message Bars & Links](../MessageBarAndLink) — banners and inline text links
- [Node Menu](../NodeMenu) — where menu items appear on real nodes
- [Form Input Controls](../InputControls) — the interactive counterparts

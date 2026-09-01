---
Name: Content Favicon Rasterization
Category: Architecture
Description: Why a node page declares TWO favicons — the node's own svg mark and a PNG rendered from it — and why Safari is the reason. Covers /api/icon, the Svg.Skia decision, the scope boundary, and the cross-repo seam.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="14" rx="2"/><path d="M3 9h18"/><circle cx="6.5" cy="6.5" r=".6"/><path d="M8 13.5l2.5 2.5L15 12l3 3"/></svg>
---

# Content Favicon Rasterization

A node page identifies itself in the browser tab: it declares **its own icon**, not the portal's.
That has shipped since 2026-08-11 — [`SeoResolver.ResolveIcon`](/Doc/Architecture/LinkPreviews)
emits the node's mark on the initial server response, before any Blazor circuit exists.

**On Safari it did nothing at all.** This page is why, and what the fix is.

## The defect

`MeshNode.Icon` for every store package is authored inline `<svg>` — a census of the plugin
repository found **56 of 56** marks in that form, the `Chess` board among them. The head therefore
carried one link:

```html
<link rel="icon" href="data:image/svg+xml,%3Csvg…" type="image/svg+xml" />
```

**Safari renders no SVG favicon.** Not from a `data:` URI, not from a URL, not in a tab, not in a
bookmark. So on every Mac and iPhone the per-content favicon was invisible — each tab wore the
portal mark — in circuit and out of it alike. A feature a whole browser cannot see is not shipped
for the people using it.

## The fix: declare both, let the browser choose

The head now declares three links for a node whose mark is svg:

```html
<link rel="icon" href="data:image/svg+xml,…" type="image/svg+xml" />
<link rel="icon" type="image/png" sizes="32x32" href="/api/icon/Chess.png?size=32" />
<link rel="apple-touch-icon" sizes="180x180" href="/api/icon/Chess.png?size=180" />
```

This is the standards-based shape, not a workaround. A browser that reads SVG prefers the scalable
icon — which is worth keeping: a vector mark beats a fixed 32 px on a retina tab strip. Safari
skips the one it cannot read and takes the PNG. Nothing is lost and one browser is gained.

`apple-touch-icon` is a **separate channel**, not a fallback: it is Safari's large bookmark /
Start-Page / Add-to-Dock tile, and with none declared Safari draws its own letter tile rather than
consulting the favicon. A node with a mark needs one pointed at that mark or its tile says nothing
about it.

The set is produced by **`SeoResolver.ResolveIconLinks`** (`memex/Memex.Portal.Shared/Seo/`).
`ResolveIcon` — the single-icon accessor — is unchanged and still returns the first of them.

## `/api/icon/{node}.png?size=N`

`SeoEndpoints.MapNodeIcon`, beside `/api/og/{node}.png`. Four properties matter:

- **One resolution, two consumers.** The route rasterizes the value `SeoResolver.ResolveIconSvg`
  returns — the same string `ResolveIcon` percent-encodes into the head's data URI. The tab's PNG
  and the head's SVG can never become pictures of different things.
- **Gated identically to the page.** It resolves through `SeoResolver.Resolve`, hence through the
  fail-closed `AnonymousGate`. A private node's mark cannot be lifted out of this route, and a
  missing node, a private one and a node with no mark all answer the same 404. There is no
  parallel permission rule here to drift from the page's.
- **Sizes are an allow-list**, not a clamped range (`IconRasterizer.SupportedSizes`). The route is
  anonymous and shared-cacheable, so a free-form `size` is an unbounded number of distinct renders
  and cache entries per node. An unsupported size answers 400 rather than snapping silently to 32.
- **No server-side cache.** Repeat cost is carried by a strong ETag (the render's own SHA-256) plus
  `Cache-Control: public, max-age=86400`, exactly as the share card does. An icon cache keyed by
  node path is an unbounded dictionary the process never frees — see
  [No Static State](/Doc/Architecture/NoStaticState).

## Why `Svg.Skia`, and why it moved SkiaSharp

SkiaSharp **draws**; it does not **parse** SVG. `OgCardRenderer` gets away with SkiaSharp alone
because it composes its card from primitives it chooses itself. An icon is the opposite: the mark
is arbitrary authored markup.

That made the scope decision explicit rather than accidental:

| option | covers | cost |
|---|---|---|
| draw generated backplates directly in Skia | generated marks only | no dependency — **but misses `Chess`, the reported case** |
| add `Svg.Skia` | every mark, authored ones included | one dependency |

The second option is tempting precisely because it avoids a dependency, and it would have shipped
an `/api/icon` endpoint Safari honours while leaving **the exact node in the bug report** still
showing the portal favicon. A fix that does not cover its own reproduction case is the shape worth
refusing, so the dependency was taken.

**Measured on the real population, not on a sample:** every one of the **56 authored store marks**
was pushed through `IconRasterizer` at both declared sizes — **56/56 produced a valid PNG of the
right dimensions, 0 blank, 0 parse failures.** That is the check worth repeating if a mark ever
stops appearing; the marks are the input this was chosen for.

🚨 **`Svg.Skia` and `SkiaSharp` are one decision, not two.** `Svg.Skia` 4.9.1 floors SkiaSharp at
3.119.2; a lower central pin under a higher transitive floor is `NU1605`, not a silent resolve — so
the central pin moved from 3.116.1 with it. Moving either version moves the other.

**Licensing** clears [the gate](/Doc/Architecture/DependencyLicensing) with nothing to add to the
allowlist: `Svg.Skia`, `Svg.Model`, `Svg.SceneGraph`, `Svg.Animation`, `ShimSkiaSharp`, `ExCSS` and
the `HarfBuzzSharp` natives are MIT; `Svg.Custom` (the SVG.NET fork) is MS-PL. Both are already
permitted.

**The `NoDependencies` posture survives.** The portal deliberately ships
`SkiaSharp.NativeAssets.Linux.NoDependencies` so the image needs no `fontconfig`/`libfreetype` and
no apt packages. The HarfBuzzSharp natives `Svg.Skia` brings are standalone and change nothing
about that.

## What is deliberately NOT rasterized

Only **inline `<svg>`** is. Two exclusions, each for a reason:

- **A mark that is already raster** (`.png`, a content-collection image, a `data:image/png` URI) —
  Safari reads those unaided, so the head declares the single link it always did and this route
  answers 404 for it. Redrawing it would add a hop and a second copy of one picture.
- **A mark that is a URL** (a content-collection file, a shipped glyph) — a location this process
  would have to fetch, over its own access-controlled route, from an anonymous request, to
  rasterize. That is a different design with its own access story; it is not in this change and no
  head advertises it.

And, unchanged from the day the feature shipped: **a node with no mark of its own synthesises
nothing.** No letter tile, no NodeType stand-in. `ResolveIconLinks` returns empty and the portal
favicon stays — the honest answer for a page with no mark, and the reason the route's 404 is never
reached from a page we serve. Redirecting to the site favicon there would *look* like a fix while
telling every consumer that this node's mark **is** the portal's.

## The cross-repo seam

The policy and the endpoint live in **core** (`memex/Memex.Portal.Shared/`); the `<head>` that
renders the links lives in **MeshWeaver.Plugins** (`Memex.Portal.Gui/Seo/SeoHead.razor`, and the
in-circuit swap in `MeshWeaver.Blazor/Pages/ApplicationPage.razor`), because the portal shell moved
there with the GUI split. So core decides *what* the links are and the shell renders them —
`ResolveIconLinks` is the seam.

That makes this a two-repo change in the ordinary direction: **core merges first**, and until the
Plugins side lands the endpoint is live and correct but only one link is declared, i.e. exactly
today's behaviour. Nothing half-lands.

### 🚨 The residue: the IN-CIRCUIT head is a separate, narrower channel

`SeoHead` renders for **anonymous requests only** — it returns early when
`HttpContext.User.Identity.IsAuthenticated`. A signed-in user's tab icon comes instead from
`ApplicationPage`'s circuit-side `<HeadContent>`, which declares one link from
`MeshNodeImageHelper.ResolveIconLink`. **That path still declares SVG only**, so a signed-in Safari
user's tab keeps the portal mark.

It is deliberately not fixed here, because it is not the same problem wearing a different hat:

- `ResolveIconLink` is **total** — a node with no icon of its own resolves to its NodeType glyph
  (`/static/NodeTypeIcons/*.svg`) and finally a neutral box. `SeoResolver.ResolveIcon` is
  deliberately **not**: the node's own mark or nothing. So the two do not resolve the same value,
  and a raster link for the in-circuit path would have to answer for shipped glyphs too — those are
  embedded resources in `MeshWeaver.Graph`, readable without an HTTP hop, so it is tractable, but it
  is a *policy* question (does a NodeType stand-in deserve a rasterized tab icon?) rather than a
  mechanical extension.
- Safari's handling of a favicon link **inserted after load** is unreliable in a way the initial
  response's is not, so the same three links added from the circuit may buy nothing.

The reported defect — the anonymous, server-rendered head that crawlers and first paint read, and
that every shared link resolves through — is fully covered. This residue is named so the next person
measures it rather than rediscovering it from a screenshot.

## Verifying it

```bash
# the head declares all three
curl -s https://memex.meshweaver.cloud/Chess | grep -oE '<link rel="(icon|apple-touch-icon)"[^>]*>'

# and the raster route really answers with a PNG of that size
curl -s "https://memex.meshweaver.cloud/api/icon/Chess.png?size=180" | file -
```

A node with no mark answering 404 on that second command is the design, not a failure — check its
head declares no icon link either. See also [Link Previews](../LinkPreviews) for the rest of the
crawler-facing head, and [Dependency Licensing](../DependencyLicensing) for the gate the new packages
pass.

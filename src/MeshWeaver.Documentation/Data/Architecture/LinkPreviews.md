---
Name: Link Previews
Category: Architecture
Description: How a mesh link unfurls in Teams, Slack or LinkedIn — the crawler-facing SEO head, the generated share card, and the one access bit that decides whether a page unfurls at all. Plus the OTHER feature called "OG card", which points the opposite way.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="14" rx="2"/><path d="M2 9h20"/><circle cx="6" cy="6.5" r=".5"/><path d="M6 13l3 3 5-5 4 4"/></svg>
---

# Link Previews

Paste a mesh link into Teams, Slack or LinkedIn and one of two things happens: a **card** — title,
description, picture — or a bare URL. This page explains what decides which, and how to get the
card. It also disentangles the two features that both answer to the name "OG card" and point in
opposite directions.

## Two features, two directions

| | Direction | What it does | Where it lives |
|---|---|---|---|
| **SEO head + share card** | **outbound** — our pages unfurling *elsewhere* | serves `og:*` meta tags and a share image to crawlers | `Memex.Portal.Shared/Seo` + `SeoHead` in the portal GUI shell |
| **`OgCard` package** | **inbound** — *other* pages rendering *inside ours* | a layout area drawing link-preview cards for external URLs or mesh nodes, embedded from markdown | the `OgCard` store package (`MeshWeaver.OgCard` module) |

When someone says "the link doesn't show a card in Teams", that is the **outbound** feature —
installing or configuring the `OgCard` package changes nothing about it, because the inbound area
renders cards *for* other pages, it does not describe *ours* to anyone.

## The outbound pipeline — what a crawler sees

A chat app's crawler fetches the pasted URL **unauthenticated** and reads the initial HTML. The
portal's first response therefore carries a crawler-facing head, rendered server-side before any
Blazor circuit exists:

1. **`SeoHead`** (in the portal shell's `App.razor`) resolves the request path to its node and
   emits `<title>`, `meta description`, canonical URL, the node's own icon, the full Open Graph
   set (`og:site_name/type/title/description/url/image`), `twitter:card`, and — for store
   plugins — Course/Product JSON-LD.
2. **`og:image`** is the node's authored image when it declares one (`PluginContent.OgImage`,
   else `poster`/`thumbnail`), and otherwise **`/api/og/{path}.png`** — a 1200×630 card the
   portal draws itself (`OgCardRenderer`, SkiaSharp with an embedded font). *Having* a share
   image is the default, not something each page remembers to author.

   **The card draws everything the node can say about itself** (`SeoEndpoints.CardContent`):
   the node's name as the title; its description — `Description`, else the content's
   `abstract`/`description`, else the catalog copy `tagline`/`summary`/`headline`; its
   category (else the type's leaf) as the eyebrow; its **own mark**, the same backplated
   `<svg>` the favicon route rasterizes, drawn large on the right; a price chip when `price`
   is positive; and the instance name plus the path in the footer. A node with no mark gets a
   **default badge** — a rounded tile in the card's accent carrying the page's initial — so no
   card is ever text on a dark rectangle (2026-09-18: the Store shared into iMessage as a bare
   title beside the site favicon). The head declares `og:image:type/width/height` for the drawn
   card (an authored image's size is unknown) and mirrors it as `twitter:image`.

   **Every page has a card.** The home page and a route that is no node share as the INSTANCE —
   `og:title` is the site name and `og:image` is **`/api/og.png`**, the site card (name + host,
   nothing read from the mesh). A node the anonymous gate withholds shares as its nearest PUBLIC
   ANCESTOR when it has one (below), and as the instance when it does not. A private page's own
   name, description and mark never reach either block.
3. **`SeoNoScriptBody`** serves the page's pre-rendered markdown inside `<noscript>`, so non-JS
   crawlers index actual content rather than an empty Blazor shell.

The **node's own icon** is part of that head, and it is declared twice — as the node's `<svg>` mark
and as a PNG rendered from it at `/api/icon/{path}.png`, because Safari renders no SVG favicon at
all. See [Content Favicon Rasterization](../ContentFaviconRasterization).

## The one bit that decides everything: anonymous read

**By default, only what an anonymous visitor may read gets a card.** `SeoResolver` gates every path
through the `AnonymousGate` and fails closed: a gated node's page serves the generic head, its
`/api/og/…` card answers 404, and a missing node and a private one are indistinguishable from
outside. This is deliberate — a link preview is served to whoever holds the link, so **a card for
a private page discloses its title, description and image past the access system**.

**That is the default and it is never inferred.** A partition whose page names are *meant* to travel
can say so, per scope, with the `publicPreview` opt-in below; nothing else turns it on, and no
heuristic ever will. The rule this page used to state — that a private page can never unfurl richly —
was the right default stated as an absolute; what it was protecting against is disclosure **without
consent**, and consent is exactly what the flag adds.

What that means in practice:

- **Every store cover unfurls.** Plugin roots are anonymous-readable by design (the cover *is*
  the marketing surface; provisioning writes the Anonymous/Public grants). Measured 2026-08-30:
  all 81 catalog covers on `memex.meshweaver.cloud` served complete cards, and cover media —
  posters, `<video>` sources — streamed anonymously with range requests.
- **The documentation unfurls.** `Doc/_Policy` carries `PublicRead = true` (it GitSyncs from the
  public MeshWeaver repository, so anonymous read reveals nothing not already on github.com).
- **A private workspace, thread or space does not unfurl — and must not.** The fix for "my link
  shows no card" is never to weaken the resolver; it is to decide whether that partition should
  be public, and say so in its `_Policy`. What a gated page under a PUBLIC root shares instead is
  the next section — and it is still not that page's own words.

A partition opts in with one bit on its seeded or authored policy:

```csharp
Content = new PartitionAccessPolicy
{
    Create = false, Update = false, Delete = false,   // still read-only
    PublicRead = true                                  // world-readable → unfurls, indexable
}
```

`PublicRead` **grants** Read to everyone including anonymous; `Read` merely **caps** (false =
deny) and never grants — see [Access Control](/Doc/Architecture/AccessControl).

## A gated page under a public root: the public-ancestor card

A **public root over gated content** is a shape the platform ships on purpose — a store listing, a
course catalog, a reporting space whose cover is the marketing surface and whose data is not. A link
into one of those used to share as the bare site card, which is the least useful thing it could say.

Measured on `www.meshweaver.cloud`, 2026-09-20:

| URL | `og:title` | `og:image` |
|---|---|---|
| `/PG3Reporting` | Fund Reporting | `/api/og/PG3Reporting.png` |
| `/PG3Reporting/Funds` | MeshWeaver | `/api/og.png` |
| `/PG3Reporting/Funds/InsuranceCore` | MeshWeaver | `/api/og.png` |
| `/PG3Reporting/Funds/InsuranceCore/2026-06-30` | MeshWeaver | `/api/og.png` |
| `/Doc/Architecture/AccessControl` | Access Control Architecture | `/api/og/Doc/…png` |

The last row is the control: **it was never about depth.** `PG3Reporting/_Policy` is a
`PartitionAccessPolicy` with a `RedirectOnDenied` of `PG3Reporting/Subscribe` and no `PublicRead`, so
the root is a public listing and everything under it is gated — and the head, gating through the
`AnonymousGate`, had nothing to say about any of it.

`SeoResolver.ResolvePublicAncestor` now walks **UP** from a withheld page to the nearest ancestor the
gate DOES admit and builds the card from that:

- **`og:title`** — the ancestor's title, then the requested path's own segments (`Fund Reporting ·
  Funds / InsuranceCore / 2026-06-30`).
- **`og:description`** — the ancestor's description, plus one localized sentence
  (`seo.gatedCard.accessRoute`) when the gated partition declares a `RedirectOnDenied`, because that
  declaration is the owner saying a route in exists. Without one, no call to action: advice that
  leads nowhere is worse than none.
- **`og:image`** — the ancestor's share image, exactly as a page of its own would declare it: its
  AUTHORED image when it has one, else the drawn `/api/og/{ancestor}.png`. Either way the same gate
  already serves it anonymously, so the unfurler can actually fetch what the head declares — and as
  on a public page, `og:image:type` and 1200×630 are declared only for the drawn card, because an
  authored banner's dimensions are unknown here.
- **`noindex, follow`** — the page's content is gated, so it is not a page to rank; the links stay
  crawlable.

### 🚨 Why this discloses nothing

**The path segments are already in the URL the sharer pasted.** Rendering them back in the title
tells the reader nothing they are not already looking at in their own chat window. Everything else on
the card belongs to the **public ancestor** and is already served to anyone who asks for it.

The withheld node's own `Name`, description, icon and content are **never read**. That is enforced by
the shape of the code, not by care: the composition
(`SeoResolver.ComposeAncestorCard`) takes an `SeoPageData` the gate ADMITTED plus the request path,
and there is deliberately no overload that takes the requested node. The redirect target is not on
the card either — it is the one thing here that is *not* in the pasted URL, and the card's link
already goes there for whoever clicks it.

`SeoPublicAncestorCardTest` holds both sides: a public page still resolves its own card unchanged, a
gated page under a public root gets the ancestor's, a gated page with **no** public ancestor still
gets nothing — and one control names the withheld nodes' own words and asserts they appear in no
field of the card.

**When no ancestor is public either, the site card stays** — unless the partition opted in (next
section). That floor is reached more often than it looks: measured on a control instance,
`/PG3/LocalHardwareOffer` unfurled as the site card **and so did `/PG3` itself**, so there was no
public ancestor anywhere on that chain and the fallback above correctly had nothing to offer. A
partition that is gated all the way up needs the opt-in, not the walk.

## `publicPreview`: letting a gated page describe itself

The question a partition owner keeps asking of a gated link is *couldn't it say the name of the node?
And the description? And the icon?* It can — once they say so, per scope:

```csharp
Content = new PartitionAccessPolicy
{
    PublicPreview = true,                       // page NAMES and summaries may travel
    RedirectOnDenied = "Offers/Subscribe",      // …and here is the way in
}
```

With it set, a page the gate still refuses emits **its own** `og:title`, `og:description` and
`og:image`, plus its own icon links, and keeps `noindex` — unfurlable without being indexable. With
it unset, which is every partition until somebody sets it, **nothing changes at all**: that negative
is the control `SeoPublicPreviewOptInTest` leads with.

### What it does NOT do

- **It grants no read.** `SeoResolver.Resolve` — the call every content-serving consumer asks — still
  refuses the page, so the BODY is never rendered for a logged-out visitor.
- **It does not publish the node.** `/sitemap.xml`, the published surface and the public-host redirect
  all read that same `Resolve`, so a previewed node stays out of every one of them. Unfurlable is not
  indexable and not published; three different questions, one of which this flag answers.
- **It is not a permission cap.** It sits beside `RedirectOnDenied` as a *disclosure* decision, not in
  the `Read`/`Create`/… family.

### The decisions, stated

- **It inherits down, nearest scope first** — like `RedirectOnDenied` and unlike the caps. A root opts
  its whole subtree in with one line; **any deeper scope opts back out with an explicit `false`**,
  which is why the field is `bool?` and not `bool`. A policy filed at one node's own scope governs
  that one node, so "settable on a single node" needs no second mechanism.
- **`BreaksInheritance` does not apply.** That flag discards inherited *role assignments*; this is not
  a role. A partition that breaks role inheritance still inherits a preview decision from above, and
  states `false` if it does not want one.
- **A child of an opted-in partition discloses too, and that is the point.** Opting a partition in
  discloses the TITLE and SUMMARY of **every node under it** to anyone holding a URL. Set it where
  page names are marketing — a catalog, an offer, a course — and never where the names *are* the
  secret (a deal room, a person's files).
- **The summary cannot leak the body.** `SeoResolver.ExtractDescription` reads six *authored summary*
  members — `Description`, `abstract`, `description`, `tagline`, `summary`, `headline` — and never
  `content` or `body`; `RenderBody` is the only thing that touches those, and no preview path calls
  it. So the description is as authored, not the first line of the page. Anything added to that chain
  in future has to be an authored summary for the same reason.
- **The picture has to be FETCHABLE, so the image routes honour the same flag.** `/api/og/{path}.png`
  and `/api/icon/{path}.png` gate through `SeoResolver.ResolveShareableNode` — the one predicate the
  head's card block also asks — because a head that declares an `og:image` the route 404s ships a
  broken card, and several unfurlers then drop the preview entirely. Everything those routes draw
  (name, authored summary, category, mark, price chip, instance + path) is exactly what the head
  discloses, which is why one flag can govern both.
- **…and an AUTHORED image or icon is the exception, because `/api/content/…` stays gated.** A store
  plugin usually authors its card as `/api/content/{partition}/content/og.png` and its mark as a
  `content:` reference. That route serves file **bytes**, not the four strings this flag consents to,
  so the opt-in deliberately does not open it — and declaring it anyway would promise exactly the
  broken picture the previous point exists to prevent. On a **previewed** page, therefore: a
  root-relative authored image falls back to the drawn card, an absolute one is kept (another host's
  business), and a content-backed icon yields **no icon link at all** — the portal favicon stays,
  which is the same honest fallback the icon route already gives a node with no usable mark. A public
  page is untouched: its authored art is fetchable precisely because the gate admits it.
- **It is revocable at the origin, and eventually-consistent at the consumer.** A previewed card and
  icon are served `private, no-store` rather than the `public, max-age=86400` a gate-admitted picture
  gets, because a policy can be withdrawn and a shared cache never re-asks the origin. So flipping
  `PublicPreview` to `false` stops the origin serving it on the next request. **It does not reach the
  unfurler's own copy** — Slack, Teams, iMessage and LinkedIn keep a preview for hours to days and no
  response header controls that (the same caching that makes a *fixed* page take hours to re-scrape,
  below). Treat the disclosure as something that outlives its withdrawal in other people's clients.

### Why an opt-in and not a behaviour

A blanket version of this would be a disclosure surface wearing a feature's colours — the same
objection that made `/health` print the PARTITION rather than the node's own name (#4258/#3890). The
difference is consent: the owner of the data states it, in the same `_Policy` that governs everything
else about that subtree, and the card is built by a pure function
(`SeoResolver.ComposePreviewCard`) that is handed the node and returns four strings plus icon links —
it carries no `MeshNode` and no `SeoPageData`, so the body the crawler-facing body component renders
cannot be reached from a preview card at all.

## The inbound `OgCard` layout area

The store package `OgCard` ships the opposite convenience: a markdown page embeds link-preview
cards for *other* targets — external URLs (their Open Graph head fetched server-side through the
core `OpenGraphPreviewService`) or same-mesh nodes (read live off the node stream):

```
@@("Org/Doc/area/OgCard?url=https://example.org/Page")
@@("Org/Doc/area/OgCard/Some/Node/Path")
```

Several targets compose into one responsive grid. It is a **module** (`MeshWeaver.OgCard`);
delisting it removes the server-side URL-fetch surface and existing embeds render the standard
area-not-found placeholder. The `/og-card` skill documents the authoring rules.

## Verifying an unfurl without pasting into chat

Fetch the page as a crawler would and read the head — the same check the platform's own sweep ran:

```bash
curl -sL -A "Mozilla/5.0 (compatible; SkypeUriPreview Preview/0.5)" \
  https://memex.meshweaver.cloud/Chess | grep -o '<meta[^>]*og:[^>]*>'
```

A page that unfurls shows the full `og:*` set and an `og:image` you can fetch anonymously. A page
that serves an empty `<title>` and no `og:*` tags is not anonymous-readable — that is the gate
working, not the feature missing. (Teams and LinkedIn cache unfurls aggressively; a fixed page
can take hours to re-scrape, and LinkedIn's Post Inspector forces a refresh.)

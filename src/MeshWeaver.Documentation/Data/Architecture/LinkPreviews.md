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
3. **`SeoNoScriptBody`** serves the page's pre-rendered markdown inside `<noscript>`, so non-JS
   crawlers index actual content rather than an empty Blazor shell.

## The one bit that decides everything: anonymous read

**Only what an anonymous visitor may read gets a card.** `SeoResolver` gates every path through
the `AnonymousGate` and fails closed: a gated node's page serves the generic head, its
`/api/og/…` card answers 404, and a missing node and a private one are indistinguishable from
outside. This is deliberate — a link preview is served to whoever holds the link, so **a card for
a private page would leak its title, description and image past the access system**. There is no
way to make a private page unfurl richly, and none should be added.

What that means in practice:

- **Every store cover unfurls.** Plugin roots are anonymous-readable by design (the cover *is*
  the marketing surface; provisioning writes the Anonymous/Public grants). Measured 2026-08-30:
  all 81 catalog covers on `memex.meshweaver.cloud` served complete cards, and cover media —
  posters, `<video>` sources — streamed anonymously with range requests.
- **The documentation unfurls.** `Doc/_Policy` carries `PublicRead = true` (it GitSyncs from the
  public MeshWeaver repository, so anonymous read reveals nothing not already on github.com).
- **A private workspace, thread or space does not unfurl — and must not.** The fix for "my link
  shows no card" is never to weaken the resolver; it is to decide whether that partition should
  be public, and say so in its `_Policy`.

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

---
nodeType: Skill
name: /og-card
description: Embed link-preview cards (OgCard) in a document — one card or a responsive grid, for external pages or mesh nodes. Covers the embed forms that actually work, how a card gets its picture and title, and the traps that make a broken card look fine (and a fine card look broken).
icon: Link
category: Skills
order: 40
---

`OgCard` renders a **link-preview card** — picture, title, description, the whole card a link — for any target: an external page (its Open Graph head is fetched server-side) or a mesh node (read live off its node stream). Several targets compose into one responsive grid, so a document embeds a whole catalog with a single reference.

# 1. The embed forms

```
@@("area:OgCard?urls=https://a.org/X,https://a.org/Y,https://a.org/Z")   ← a responsive GRID
@@("area:OgCard?url=https://a.org/Page")                                 ← ONE external card
@@("area:OgCard/Some/Node/Path")                                         ← ONE mesh-node card
```

- **`area:OgCard`** (colon) and **`area/OgCard`** (slash) both resolve. The colon form is what live documents use.
- **`?urls=` is comma-separated** and is the multi-target form. Mesh paths and external URLs can be mixed in one list.
- **`?url=` (singular) never splits** — a comma in it is data. Use it for a URL that genuinely contains a comma.
- A literal comma inside a URL in a `?urls=` list must be double-encoded (`%252C`), because the separator is honoured both raw (`,`) and percent-encoded (`%2C`).
- Entries may be percent-encoded; plain URLs work as written.

A single target renders width-bounded like a chat unfurl; two or more render as a grid of `minmax(280px, 1fr)` columns.

# 2. Where a card's content comes from

| Card field | External target | Mesh-node target |
|---|---|---|
| Title | `og:title`, else `<title>`, else the URL's **last path segment** | the node's `Name` |
| Description | `og:description`, else `<meta name="description">` | the node's `Description` |
| Picture | the page's declared **icon** → `og:image` → `/favicon.ico` | the node's own icon |
| Link | the URL (new tab) | `/{nodePath}` |

**The picture is the ICON, not the `og:image` poster.** The card's image box is a fixed 48 px square with `object-fit: cover`; a 1200×630 banner cropped into that is a meaningless sliver, while an icon is drawn for exactly that size.

**A MeshWeaver page's icon is the NODE's icon.** The per-page SEO head emits `<link rel="icon">` from `MeshNode.Icon`, so a card for a node page shows that node's mark rather than the portal logo. A node that carries no icon of its own keeps the portal favicon — nothing is synthesised.

# 3. 🚨 The traps

### An MCP `get` of the area is NEVER a pass/fail signal

Every target renders **immediately** as a placeholder card and fills in when its fetch lands — one slow target must never block the grid. A one-shot read of the area returns **frame 0**, which is *always* the placeholder. A card that is perfectly healthy and a card that is permanently broken are byte-identical in that frame.

**Only a browser render tells you whether a card works.** Do not report an area snapshot as evidence either way.

### The placeholder's title is the URL's last path segment — learn to recognise it

A card showing **`Ifrs17`** with no description, where the page declares `og:title` **`IFRS 17`**, is not a "wrong title" — it is the card **still on its placeholder**, i.e. the fetch has not landed. The two failure modes look the same on screen, so read the title against the target's real `og:title` before concluding anything.

### A successful-but-useless 200 must not be cached

A portal mid-restart, a login wall, or any SPA catch-all answers **200 with its shell page** and no `og:*` tags. Nothing throws, so an exception-eviction path never fires. The preview cache is therefore gated on the page having declared **`og:title`** — not on having *a* title (the shell has one, which is exactly how it used to slip through) and not on having an icon (practically every page yields one). An unresolved preview is evicted and re-fetched on the next view.

### Relative icons resolve against `<base href>`, not the page URL

Every Blazor portal serves `<base href="/">` plus a relative `href="favicon.ico"`. Resolved against the *page* URL that yields `/Some/Section/favicon.ico`, which a catch-all-routed SPA answers **200 text/html** rather than 404 — a silently broken image that looks like a working link.

### A `data:` URI icon works in browsers and NOT in email

A node whose icon is an **inline `<svg>`** publishes it as a `data:image/svg+xml,…` URI. Every browser renders that — tab, card, unfurl. **Email does not.** Outlook renders through Word, which supports no inline SVG at all, and classic Outlook for Windows blocks `data:` URIs outright; the icon silently becomes a broken image or nothing.

So **any email or export path needs a RASTER icon at an `http(s)` URL** — never the head's `data:` URI. Two things already on that side of the line and safe to use:

- **`/api/og/{nodePath}`** — the generated PNG share card, an ordinary cacheable http URL. It is a wide card, not a square icon, but it is a real raster.
- A node whose icon is stored as a **file** (`content:mark.png`) already resolves to an http content-route URL rather than a data URI, so it is email-safe as authored.

For a square raster icon of an inline-SVG node there is currently **no route** — the icon exists only as markup in the head. Rasterising it is a separate endpoint, not something to fake by inlining.

### Ties between icons are broken by the LAST declaration

Site chrome emits its favicon early; a page that declares its own icon emits it later. Preferring the first would mean a page could never override the site-wide mark, and every card in a grid would draw the same portal logo.

### Only anonymous-readable pages have a head worth fetching

The rich SEO head (`og:*`, the node icon) is emitted for **anonymous** requests to anonymous-readable nodes. A private page serves the generic shell, so its card falls back to the URL's last path segment. That is correct behaviour, not a bug — publish the page if you want it to preview.

# 4. Verifying a card

1. **Check the target's head first** — this is the input, and it is one command:
   ```bash
   curl -sL https://portal.example.org/SomePage | grep -o '<meta property="og:[^>]*>'
   curl -sL https://portal.example.org/SomePage | grep -o '<link rel="icon"[^>]*>'
   ```
   No `og:title` ⇒ the card can only ever show the URL's last segment. Fix the page, not the card.
2. **Open the document in a browser.** That is the only real test (see the traps).
3. **If a card is stuck on its placeholder**, look for the one log line that distinguishes the two silent failures — the message text is `Open Graph fetch of {Url}` (a *space* in "Open Graph"; grepping `opengraph` matches only the logger category, which some log formats omit):
   ```bash
   kubectl -n <ns> logs <portal-pod> -c memex-portal --since=30m | grep -E 'no og: metadata|Open Graph'
   ```
   A warning ⇒ the fetch faulted. An info "returned no og: metadata" ⇒ it succeeded but the page carried nothing. **Neither line ⇒ the fetch was fine**, and the problem is downstream of the fetch.

# 5. Guard rails

- The fetcher refuses anything that is not `http(s)`, and refuses literal **and DNS-resolved** loopback / private / link-local addresses — card targets are author-supplied, so this is the SSRF boundary. A public URL that 302s to a private one is not followed.
- Each URL is fetched **once per process** and replayed to every card; a failed or metadata-less fetch evicts its entry so the next view retries once. No timer, no retry loop.
- Only the page **head** is parsed; the fetch is bounded in both bytes and time.

---
Name: Public Web Presence
Category: Architecture
Description: How the portal is also the company's public web site — one canonical host for everything a signed-out visitor may read, a body in the first HTTP response so a crawler can read it, a sitemap that descends to every public page, and the measurements that showed none of it held before.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18"/><path d="M12 3a14 14 0 0 1 0 18"/><path d="M12 3a14 14 0 0 0 0 18"/></svg>
---

# Public Web Presence

The portal serves two audiences from one process: people who sign in and work, and everybody else,
including search engines. This page is the design for the second audience. It replaces a separate
static marketing site, and it is the reference for every question of the form "why is this page not
on Google".

## What was measured on 2026-09-11

The three brand hosts had already collapsed into one: `systemorph.com`, `www.systemorph.com`,
`meshweaver.cloud` and `portal.meshweaver.cloud` all answered `301` to `www.meshweaver.cloud`, a
static one-page nginx site with no meta description, no Open Graph card, no `robots.txt` and no
sitemap. It was the only MeshWeaver page Google showed. Google also still listed the old WordPress
addresses on `systemorph.com` (`/team/`, `/contact/`, `/imprint/`, `/privacy-policy/`), every one of
which landed on the homepage, which a search engine files as a soft 404.

`memex.meshweaver.cloud`, the portal, had good crawler plumbing in its `<head>`: a per-page title,
description, canonical link, Open Graph card, Course and Product JSON-LD for store items, a real
`robots.txt` and a sitemap of 103 public roots. It had **zero pages in Google's index**, and the
reason was in the `<body>`:

```
$ curl -sA Googlebot/2.1 https://memex.meshweaver.cloud/Doc/Architecture/Localization
<title>Localization · Memex</title>
<link rel="canonical" href="https://memex.meshweaver.cloud/Doc/Architecture/Localization" />
…
<body>
  <div id="components-reconnect-modal"> Reconnecting… </div>
  <script src="_framework/blazor.web.js" autostart="false">
</body>
```

A Blazor Server page ships an empty body and fills it over the SignalR circuit. Googlebot runs
JavaScript, but it does not hold a circuit, so it indexed nothing. Three separate defects stacked
up behind that one symptom:

1. **The only body source was the node-level mirror.** `SeoNoScriptBody` rendered
   `MeshNode.PreRenderedHtml` inside `<noscript>`. That mirror is set by the file-system and
   partition storage readers, and only when the content deserialises as a typed `MarkdownContent`.
   The page resolver issues a mesh-wide `path:` query, which on the partitioned Postgres deployment
   runs through the cross-schema reader, and that reader never sets the mirror. A plugin cover
   (`PluginContent.body`) never had one to begin with. So even the noscript fallback was empty.
2. **The interactive page skipped prerender for strangers.** `ApplicationPage` fetched cached HTML
   during prerender only for authenticated visitors, on the theory that serving it to a stranger
   could leak a page the anonymous gate would later refuse. The gate already decides the head; the
   body had simply never been wired to the same decision.
3. **The sitemap stopped at the roots.** It listed partition roots of three node types, plus each
   store plugin's declared `publicSegments`, read through the raw storage adapter from an
   anonymous HTTP entry. On the partitioned deployment that read returned nothing, so no course
   chapter was ever listed; and nothing below `Doc` was ever a candidate at all.

Smaller findings in the same sweep: a path that does not exist renders its nearest ancestor with
HTTP 200 (the canonical link limits the damage, the status does not); `HEAD` answered 405 from
every page; the default site name was "Memex Portal" because `Portal:SiteName` was unset on the
public instance; the signed-out landing (`/welcome`) was a generic sign-in page with half of its
copy hard-coded in English.

## The shape

One process, two hosts, one address per page.

| Host | Role | Indexed |
|---|---|---|
| `www.meshweaver.cloud` | **Public.** The landing, the documentation, the store, every course cover and free chapter, every public space. Canonical for all of it. | yes |
| `memex.meshweaver.cloud` | **App.** Sign-in, the workspace, `/api`, `/mcp`, gRPC, the plugin registry. A stranger asking it for a public page is sent to the public host with `301`. | no |
| the brand hosts | `301` to the public host, per path, so the old WordPress addresses land on real pages. | — |

Why not rename the portal host to `www`: the Entra, GitHub, Google, Apple and LinkedIn redirect
URIs, every other deployment's plugin-registry URL, every MCP client configuration and every shared
link name the app host, and each of those is its own outage class. The two-host shape gets the same
public result and leaves that decision open.

### Configuration

Two keys, both read through `PublicSite` and both off by default so a single-host deployment is
unchanged:

- `Portal:PublicHost` — the public host name (`www.meshweaver.cloud`). When set, the canonical
  link, `og:url`, every sitemap `<loc>` and the `Sitemap:` line of `robots.txt` are built on
  `https://{PublicHost}` whichever host served the request; a request on any other host is on the
  **app host**, whose `robots.txt` reads `Disallow: /` and whose pages carry `noindex`.
- `Portal:LandingPath` — the node a signed-out visitor sees at `/` (the landing Space). Unset, the
  root stays the portal's own welcome route.

### Publicness is decided by one gate, per node

Nothing here has its own notion of "public". A page is public because its node carries an
Anonymous Read grant, and `AnonymousGate` is the single instrument that reads it. The head, the
body, the sitemap, the Open Graph card and the app-host redirect all ask that gate for the exact
node in question and withhold everything on a refusal or on an undetermined answer. That is what
makes descending the tree safe: the installer expresses a course's free and paid chapters as a root
grant plus a deny on every chapter that is not free (see `PackageInstaller.EnsureDeclaredAccess`),
and a commercial plugin's cover can be public while its content is not. Asking per node is both the
fix for the empty sitemap and the reason the body can be served.

### The body is in the first response

`SeoPageData.Body` is the page text a crawler reads: the node's mirrored HTML when it carries one,
else the content's own `prerenderedHtml`, else its markdown (`content` for a markdown node, `body`
for a plugin cover) rendered through the portal's own pipeline, anchored on the node so links
resolve as they do for a signed-in visitor. It is computed only for a node the gate admitted, and it
is rendered **visibly** in the static server pass of the page, not inside `<noscript>`: Googlebot
renders with JavaScript on and may ignore noscript content, and the visible article is what the
interactive circuit replaces on hydration. The interactive page reads the same per-request stash
the head resolved into, so the mesh is asked once per request.

### The sitemap descends

For every root the gate admits, `SeoEndpoints.EnumeratePublished` lists the root's descendants
whose node type is a **page** — `Markdown`, `Space`, `Store/Plugin`, `Store/Catalog`, `Edu/Module`,
`Edu/Page` — skipping any path with a satellite segment (`_Thread`, `_Access`, `_GitSync`, `Source`,
`Test`, `Release`), and asks the gate about each. A reinsurance plugin's partition carries hundreds
of amount types, cashflows, source files and release markers; none of them is a page, and listing
them would bury the twenty that are. The listing runs as System (a stale negative here costs a URL,
never a leak, because every candidate still passes the gate), the gate checks run eight at a time,
and the same enumeration feeds **Administration → Published to the web**, so the list a person
reads and the list a crawler gets cannot drift.

### The rest of the crawl surface

- A path with a remainder beyond the resolved node (a layout-area route, or a missing page that
  fell back to its ancestor) carries `noindex` and a canonical link to the node it actually
  rendered, so a fallback never competes with the real page.
- `HEAD` is answered as a `GET` with the body discarded (`PublicSite.UseHeadAsGet`).
- The public host's `robots.txt` keeps `/login`, `/welcome`, `/api/`, `/_blazor` and `/dev/` out.
- Course covers keep their `Course` JSON-LD with Systemorph as the provider and the price as an
  `Offer`; the English and German twins of a course point at each other with `hreflang`.

## Verifying a deployment

Each check is one command, and each has a negative control: run it on the app host too, where the
answer must differ.

```
# the body: the article text, not the reconnect modal
curl -sA Googlebot/2.1 https://www.meshweaver.cloud/Doc/Architecture/Localization | grep -c '<article'
# the sitemap descends: a chapter and a doc page are listed
curl -s https://www.meshweaver.cloud/sitemap.xml | grep -c 'AgenticPrimer/01-TheMagicWish'
curl -s https://www.meshweaver.cloud/sitemap.xml | grep -c 'Doc/Architecture/'
# the app host sends strangers away and keeps crawlers out
curl -sI https://memex.meshweaver.cloud/Doc | grep -i '^location: https://www.meshweaver.cloud/Doc'
curl -s https://memex.meshweaver.cloud/robots.txt | grep -c 'Disallow: /$'
# HEAD is answered
curl -sI https://www.meshweaver.cloud/Doc | head -1
```

Search Console's URL inspection, "rendered HTML" view, is the acceptance test for the whole design:
it must show the article text for a documentation page, a course cover, a course lesson and a plugin
cover. Indexing before that view shows text only fills the report with "crawled, currently not
indexed".

## Related

- [Access Control](../AccessControl) — the anonymous grant and the gate.
- [Localization](../Localization) — the landing renders as authored, in English and German.
- [Operating from the portal](../OperatingFromThePortal) — how the ingress change that adds the
  public host is rolled.

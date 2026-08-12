---
NodeType: Markdown
Name: "PDF Export — one browser, two fidelities"
Abstract: "Every PDF MeshWeaver exports is printed by the headless Chromium in the portal image. The two fidelities differ in the DOCUMENT handed to it: content-faithful composes the markdown AST into a structured, branded print document — cover page, contents, running header and footer in CSS Paged Media — while pixel-faithful composes the deck's own live stage so gradients, raw HTML and transforms survive. One engine, two documents; the choice is fidelity, not capability."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#4f46e5'/><rect x='4' y='6' width='16' height='9' rx='1.5' fill='#fff'/><rect x='5.5' y='7.5' width='13' height='6' rx='1' fill='#818cf8'/><rect x='9' y='17' width='6' height='1.8' rx='.9' fill='#fff'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Export"
  - "Decks"
---

> **Read first:** [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — the headless browser is a `Process` leaf and obeys those rules exactly. For what a deck and a slide *are*, see [Slides & Decks](/Doc/GUI/SlidesAndDecks).

## Two documents, one engine

`Deck → Export to PDF` has two renderers, and the choice between them is a real product decision, not a migration:

| | **Content-faithful** (default) | **Pixel-faithful** (opt-in) |
|---|---|---|
| How | Markdig AST → `Document` model → print HTML + CSS Paged Media → browser | Slides → one self-contained HTML doc carrying the live stage CSS → browser |
| Text | Selectable, searchable | A picture of text |
| Size / speed | Small, fast | Larger, a few seconds |
| Carries | Headings, lists, tables, links — plus **cover page, contents, running header/footer, page breaks** | **Everything the browser draws** — gradients, background images, raw HTML, CSS layout, transforms, web fonts |

**Both print with the same browser.** Until #1230 the content-faithful path drew its PDF with a document model (QuestPDF), whose Community tier is free only below a revenue threshold — a licence MeshWeaver cannot ship under. Its replacement is the engine already in the image, so there is now ONE PDF back end instead of two, and the page furniture is expressed in CSS rather than in a fluent drawing API.

**Content-faithful stays the default because for most documents it is the better artifact**: structured and text-selectable, and the only one that carries a cover page, a contents list and a running header. Pixel fidelity matters for one specific class of deck — the design-led, HTML-authored one, where the meaning *is* the rendering.

## What the document model structurally cannot do

`SlideContent` has a `Background` field holding **raw CSS** (`linear-gradient(135deg, #667eea 0%, #764ba2 100%)`), and its `Content` is markdown through which **raw HTML and inline SVG pass unchanged**. The live stage takes both and hands them to a browser.

The `Document` model has neither concept. It is a typed element tree — heading, paragraph, table, list — and there is no element that means "a gradient" or "this author's `<div style="transform: rotate(-3deg)">`". Printing that model as HTML did not change this: the content-faithful path still reconstructs the *content*, so a deck's own CSS is still not what lands on the page. **Reproducing an author's rendering requires printing the author's document**, which is exactly what the pixel path does and why it remains a separate choice rather than a bug fix.

## The pipeline

```
Deck node                                       Markdown node (content-faithful)
  └─ DeckLayoutAreas.ResolveDeckSelection         └─ DocumentBuilder.Build
       └─ SlidePrintComposer.Compose                   ← Markdig AST → Document model,
            ├─ MarkdownViewLogic.Render                  page-break rules, TOC headings
            └─ SlidePrint.{html,css} templates      └─ DocumentPrintComposer.Compose
       └─ inline api/content assets as data:             ← Document → print HTML,
            URIs (read under the user's identity)          MarkupNode tree + the
                                                           DocumentPrint.{html,css} templates
       └──────────────┬──────────────────────────────────────────┘
                      └─ IPixelPdfRenderer.Render      ← the ONLY browser step, shared
                           └─ IIoPool(Process).InvokeBlocking  ← off the hub, bounded
```

Both composers are **pure, synchronous, no IO, no browser**, and both build markup through `MarkupNode` — the one place in the assembly that turns a tree into a string of HTML, escaping text and attribute values on the single path out. Neither builds HTML by string interpolation, and the page furniture lives in real `.html` / `.css` template files rather than in C#.

**Nothing visual is re-invented.** The slide body is rendered by `MarkdownViewLogic.Render` — the very renderer the portal uses, so raw HTML and SVG behave identically. The stage styling comes from `SlideLayoutAreas.ThemeTokens` (made `public` for exactly this consumer) plus a stylesheet that mirrors `BuildStage`: same 16:9 box, same padding ratios, same type scale. Copying the theme into the exporter would have let the printed deck and the on-screen deck drift; referencing the one declaration means they cannot.

**Markup lives in template files**, not in C#. `SlidePrint.{html,css}` / `SlidePrintSection.html` for the deck, `DocumentPrint.{html,css}` for the document, all embedded resources with named placeholders that the composers substitute into. The document stylesheet is where the cover, contents, header, footer and page-break rules are actually written, so they are reviewable as CSS rather than buried in a fluent API.

### The page box IS one slide

The print stylesheet sets `@page { size: 13.333in 7.5in; margin: 0 }` — the standard 16:9 presentation page, exactly **1280 × 720 CSS px** at 96 dpi. Two things follow, and both matter:

- One slide is one page, at 1:1 scale. No letterboxing, no shrink-to-fit.
- `vw` resolves against a 1280 px viewport, so the `font-size: clamp(18px, 3vw, 42px)` sizing the [slide guide](/Doc/GUI/SlidesAndDecks) recommends behaves in print exactly as it does on the stage.

### The one line the whole feature rests on

```css
* { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
```

Chromium **discards every background paint when printing** unless colour adjustment is forced to exact. Without those two declarations a gradient deck prints as white pages — the feature would look implemented and produce nothing. It is pinned by a test for that reason.

### A pixel export IS the slides — nothing else

The browser prints the deck's own stage, so there is no document structure to hang branding on: a pixel-faithful export has **no cover page, no table of contents, no running header or footer, and no page-break rules**. Those belong to the composed print document of the content-faithful path — which owns its `@page` rules — and cannot be glued onto a deck whose page box IS one slide. The export dialog therefore **hides** them when pixel fidelity is selected, rather than leaving controls on screen that would silently do nothing.

If a deck needs a branded cover, the honest answer today is to make the cover a slide — where the deck's own CSS can style it far better than the document model could.

### 🚨 The browser is inside the trust boundary — so the document denies everything

This is the hazard that comes with server-side rendering, and it is not theoretical. The headless browser runs **as the portal**: it can reach internal services, private subnets and cloud metadata endpoints that the deck's author could never reach from their own browser, and it can read local files over `file:`. Slide bodies are **user-authored and pass raw HTML through verbatim**. An `<img src="http://169.254.169.254/…">` or a `Background: url(file:///…)` is all it would take.

Two independent layers close it:

1. **The print document's Content-Security-Policy** — `default-src 'none'`, with `img-src data:` / `font-src data:` / `style-src 'unsafe-inline'` and nothing else. Those are exactly what a pixel print legitimately needs: assets the composer has *already inlined* as `data:` URIs, the inline stylesheet, and the slides' own `style=""` attributes. Scripts, connections, frames, objects and every remote or `file:` origin are denied. The `<meta>` **must stay the first element in `<head>`** — a policy declared after content does not govern that content.
2. **Process-level network denial** — the browser is launched with no name resolution (`--host-resolver-rules=MAP * ~NOTFOUND`) and every http/https request pointed at a dead proxy. A self-contained document has no legitimate use for either, so taking them away means a lost or bypassed policy still cannot reach a routable target.

Each layer is tested **with the other neutralised**, and each test demands a live control leak before believing the protected run (`PixelRenderIsolationTests`). That is not ceremony: the first draft asserted the CSP while the process flags were quietly doing the blocking, and would have passed with no CSP at all. A CSP nobody tested is a comment.

**What this costs.** A slide can no longer pull in a **remote** image (`<img src="https://example.com/logo.png">`). That is the intended trade — that fetch *is* the SSRF surface, and it was being made by the server, not by the reader. The supported way to put a picture in a deck is to store it alongside the slides, where the export inlines it and the PDF ends up self-contained anyway. If remote assets are ever genuinely wanted, the answer is **not** to widen the policy: it is to fetch them server-side against an allowlist, under the portal's own egress rules, and inline them like any other asset — a deliberate feature, not a relaxed directive.

### Assets are inlined, not linked

Images in slides resolve to `api/content/{collection}/{path}` — an **access-controlled portal route**, which a `file://` document cannot fetch. So the export collects every such reference, reads it through `IContentService` **under the exporting user's identity**, and rewrites it to a `data:` URI. The printed deck is self-contained and contains only what that user could already read. An asset that cannot be read is left as a link and logged: a missing picture prints broken, exactly as it would on screen — it never fails the export.

### Embedded layout areas are resolved before printing — the browser cannot fill them

A slide can embed a live view with `@@(…)`. The markdown pipeline emits an **empty** anchor
(`<div class='layout-area' …></div>`) and leaves the filling to a live client — which works on
screen and is exactly wrong here. This print document is loaded from `file://` under
`default-src 'none'`, with the browser's resolver pointed at nothing and its proxy pointed at a
dead port. The browser therefore *cannot* fill the anchor, and never could: an embedded view
printed as a **silent blank page region**.

So the export resolves every embed **server-side, before printing** — the same
`LayoutAreaResolver` the markdown and email exports use — and it runs **before** asset inlining,
so any content the resolved area itself references gets inlined too. This is deliberately the same
isolation story as everything else here: the resolved markup is produced by MeshWeaver from streams
the exporting user may read, never fetched by the browser.

The DOM is only round-tripped when an anchor is actually present, so a deck with no embeds prints
byte-identically to what the composer produced.

## Where the browser runs — it IS in the image

**The portal image ships a headless Chromium** (`deploy/base-images/portal-ai/Dockerfile`). It has to: since #1230 the browser is not an optional extra for a minority of decks, it is the PDF renderer. A portal whose PDF export depended on an operator having installed Chrome by hand would have no PDF export.

Three things about that image are load-bearing, and each was measured rather than assumed:

- **It is the headless *shell*, not the full browser.** The desktop build never finishes a print in a container: it blocks in start-up paths that want dbus, UPower, GSettings and the component updater, none of which exist there — 150 s in, no PDF, process still alive. The headless shell has none of that machinery, prints the same document in about two seconds, and is roughly half the size.
- **It comes from Playwright, not from `apt`.** The base is Ubuntu 24.04, where `chromium-browser` is a snap shim that installs no browser at all in a container and there is no plain `chromium` deb; Google Chrome and Microsoft Edge have no Linux arm64 build, so either would have produced a green amd64 leg and an arm64 leg with no browser. Playwright publishes a build for both, and its binary is even named differently per architecture — the Dockerfile matches both names and then runs `--version`, so a mismatch fails the BUILD rather than a user's export.
- **The generic font families are pinned to faces that have a bold.** The CJK coverage font that Playwright's dependency list installs claims `sans-serif` and `system-ui` and ships a Regular face only, so before `fonts-local.conf` existed every heading, table header and bold run printed at regular weight — silently. The print stylesheet names concrete families too, so the renderer does not depend on the host's fontconfig either.

`PixelRenderingOptions` still resolves the executable at runtime, first hit wins, so a deployment can point at a different browser:

1. `MarkdownExportConfig.PixelRendering.ExecutablePath`
2. `MESHWEAVER_CHROMIUM_PATH`, `CHROME_BIN`, `PUPPETEER_EXECUTABLE_PATH` — the image sets `CHROME_BIN`
3. Well-known install locations per platform (`/usr/bin/chromium` — where the image symlinks it — `/usr/bin/google-chrome`, the macOS app bundles, the Windows Program Files paths)

A configured-but-missing `ExecutablePath` reports **unavailable** and logs a warning rather than falling through to auto-detection — an operator who pointed at the wrong path must see that, not silently get a different browser.

### The sandbox, and why `--no-sandbox` is not a preference here

The portal container runs as **uid 0**, and Chromium flatly refuses to start as root without `--no-sandbox`:

```
Running as root without --no-sandbox is not supported.
```

So the renderer passes the flag when `NoSandbox` is set **or** when the process is actually running as root — read from `geteuid()`, not inferred from a user name, so a container with no passwd entry answers correctly. That is not overriding an operator decision: where a decision exists (a non-root deployment, where the namespace sandbox does work) `NoSandbox` is still theirs. Where the browser cannot use its sandbox by construction, the flag is the only way to run at all, and the isolation that actually holds is the layer above it — the print document's `default-src 'none'` CSP plus the process-level network denial, both described above and both tested with the other neutralised.

## Capability, not an error

The export dialog offers the fidelity choice **only when the server has confirmed both** that the node is a Deck *and* that a browser resolves. `ExportDocumentLayoutArea` subscribes `IPixelPdfRenderer.Probe()` — promise-cached in the renderer, so the file-system probe runs once per mesh and replays — and enriches `ExportDocumentControl.PixelFidelityAvailable` when the answer lands.

On the portal image the probe always succeeds, so the choice is always offered. The "no browser here" contract still matters and is still tested: a MeshWeaver embedded somewhere without one gets a **loud, actionable failure** from either fidelity rather than a quiet downgrade. A user who asked for a PDF must never receive something that lost its formatting while believing otherwise.

## Threading — the browser is an ordinary I/O leaf

Both the probe (`File.Exists`) and the print (a subprocess) go through `IIoPool`'s `Process` pool via `InvokeBlocking`, exactly like [`GitCli`](/Doc/Architecture/ControlledIoPooling). Consequences worth naming:

- The work runs **off the hub scheduler** — a burst of exports can never spawn an unbounded pile of browsers; the pool's cap (4 by default) is the governor.
- Unsubscribe or mesh teardown **kills the whole browser tree**, so a pool slot is never held by an orphaned renderer child.
- Each print gets its **own scratch directory** — HTML in, PDF out, and a throwaway browser profile. A shared profile would serialise concurrent prints (Chromium locks it) and leak state between exports.
- A browser that wedges is bounded by `PixelRenderingOptions.Timeout` and killed. That bound exists to stop a hung browser holding a slot — it is not a retry, and raising it is never the fix for a browser that does not finish.

The public surface is `IObservable<T>` throughout; there is no `Task` in a signature and no `Observable.FromAsync` anywhere.

## Testing without a browser

Every decision about *what the page looks like* happens in a composer that is pure and synchronous — `SlidePrintComposer` for decks, `DocumentPrintComposer` for documents — so both contracts are pinned by ordinary unit tests that need nothing installed (`SlidePrintComposerTests`, `DocumentPrintComposerTests`). That is where "is the cover suppressed when the brand has no name", "does the contents entry link to an id a heading actually carries" and "can a brand header containing `</style>` break out of the stylesheet" are answered.

The browser tests (`HeadlessChromiumRenderTests`, `RendererOutputTests`, `PdfHeaderLogoTests`, `DeckPixelExportScriptRelayTest`) ask the renderer's own probe and then assert **whichever contract applies to the machine**: with a browser, a real PDF — cover, contents and body on separate pages, the running header on every page but the cover, `N / M` in the footer; without one, a loud, actionable refusal. They are never skipped, and a missing browser can neither hide a regression nor redden a build. CI provisions the same browser build the image ships, as an explicit infrastructure step, so a shard cannot pass by taking the "no browser" branch.

### 🚧 The one thing that did not survive: page numbers in the contents list

The document model could print `Chapter 3 ......... 12`. **The browser cannot**, and the contents list therefore carries clickable links but no page numbers.

The reason is specific: reading a target's page from CSS needs `target-counter()` from CSS Generated Content for Paged Media, and Chromium implements no part of it — verified directly against the browser this image ships, alongside the features that *do* work (`@page` margin boxes, named pages, `counter(page)` / `counter(pages)`), which is what the rest of the furniture is built on.

The alternative is a two-pass print: print once, read back from the PDF's link annotations which page each anchor landed on, inject the numbers and print again. It was rejected deliberately — it doubles the cost of every export with a contents list, and its failure mode is *silently wrong page numbers* when the inserted digits reflow the list. An honestly absent column beats a confidently wrong one, and in a PDF the clickable entry is the affordance a reader actually uses.

## See also

- [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — the rules the browser leaf follows
- [Slides & Decks](/Doc/GUI/SlidesAndDecks) — `SlideContent.Background`, the manifest, and the live stage
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — exports run as scripts with inputs, progress and output

---
NodeType: Markdown
Name: "Pixel-Faithful Export — when the PDF has to look like the screen"
Abstract: "Deck → PDF is content-faithful by default: the markdown AST is rebuilt into a QuestPDF document, which is why it is fast, small, text-selectable and needs no browser. That model has no notion of a CSS gradient, a raw-HTML slide body or a transform, so design-led decks lose exactly what they were designed for. The pixel-faithful path composes the deck into one self-contained HTML document carrying the LIVE stage CSS and prints it with a headless browser — augmenting the fast path, never replacing it, and shipping the capability without shipping the browser."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#4f46e5'/><rect x='4' y='6' width='16' height='9' rx='1.5' fill='#fff'/><rect x='5.5' y='7.5' width='13' height='6' rx='1' fill='#818cf8'/><rect x='9' y='17' width='6' height='1.8' rx='.9' fill='#fff'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Export"
  - "Decks"
---

> **Read first:** [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — the headless browser is a `Process` leaf and obeys those rules exactly. For what a deck and a slide *are*, see [Slides & Decks](/Doc/GUI/SlidesAndDecks).

## Two exports, deliberately

`Deck → Export to PDF` has two renderers, and the choice between them is a real product decision, not a migration:

| | **Content-faithful** (default) | **Pixel-faithful** (opt-in) |
|---|---|---|
| How | Markdig AST → `Document` model → QuestPDF | Slides → one self-contained HTML doc → headless browser prints it |
| Text | Selectable, searchable | A picture of text |
| Size / speed | Small, fast | Larger, a few seconds |
| Needs | Nothing | A Chromium/Chrome/Edge on the server |
| Carries | Headings, lists, tables, links, images | **Everything the browser draws** — gradients, background images, raw HTML, CSS layout, transforms, web fonts |

**Content-faithful stays the default because for most decks it is the better artifact.** Pixel fidelity matters for one specific class of deck — the design-led, HTML-authored one, where the meaning *is* the rendering. Making every export pay for a browser to serve that minority would be the wrong trade, which is why this augments the fast path instead of replacing it.

## What the document model structurally cannot do

`SlideContent` has a `Background` field holding **raw CSS** (`linear-gradient(135deg, #667eea 0%, #764ba2 100%)`), and its `Content` is markdown through which **raw HTML and inline SVG pass unchanged**. The live stage takes both and hands them to a browser.

`PdfDocumentRenderer` has neither concept. It walks a typed element tree — heading, paragraph, table, list — and there is no element that means "a gradient" or "this author's `<div style="transform: rotate(-3deg)">`". So the gap is not a missing feature in the PDF renderer; it is that **reproducing browser rendering requires a browser**. That is the whole argument for this path, and the reason it is opt-in rather than the fix for a bug.

## The pipeline

```
Deck node
  └─ DeckLayoutAreas.ResolveDeckSelection      ← the SAME order the live views use
       └─ SlidePrintComposer.Compose            ← pure, synchronous, no IO, no browser
            ├─ MarkdownViewLogic.Render          ← the framework's own markdown pipeline
            └─ SlidePrint.{html,css} templates   ← + SlideLayoutAreas.ThemeTokens
       └─ inline api/content assets as data: URIs (read under the user's identity)
       └─ IPixelPdfRenderer.Render               ← the ONLY browser step
            └─ IIoPool(Process).InvokeBlocking   ← off the hub scheduler, bounded
```

**Nothing visual is re-invented.** The slide body is rendered by `MarkdownViewLogic.Render` — the very renderer the portal uses, so raw HTML and SVG behave identically. The stage styling comes from `SlideLayoutAreas.ThemeTokens` (made `public` for exactly this consumer) plus a stylesheet that mirrors `BuildStage`: same 16:9 box, same padding ratios, same type scale. Copying the theme into the exporter would have let the printed deck and the on-screen deck drift; referencing the one declaration means they cannot.

**Markup lives in template files**, not in C#. `SlidePrint.html`, `SlidePrint.css` and `SlidePrintSection.html` are embedded resources with named placeholders; the composer substitutes into them. There is no HTML-string building anywhere in the path.

### The page box IS one slide

The print stylesheet sets `@page { size: 13.333in 7.5in; margin: 0 }` — the standard 16:9 presentation page, exactly **1280 × 720 CSS px** at 96 dpi. Two things follow, and both matter:

- One slide is one page, at 1:1 scale. No letterboxing, no shrink-to-fit.
- `vw` resolves against a 1280 px viewport, so the `font-size: clamp(18px, 3vw, 42px)` sizing the [slide guide](/Doc/GUI/SlidesAndDecks) recommends behaves in print exactly as it does on the stage.

### The one line the whole feature rests on

```css
* { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
```

Chromium **discards every background paint when printing** unless colour adjustment is forced to exact. Without those two declarations a gradient deck prints as white pages — the feature would look implemented and produce nothing. It is pinned by a test for that reason.

### Assets are inlined, not linked

Images in slides resolve to `api/content/{collection}/{path}` — an **access-controlled portal route**, which a `file://` document cannot fetch. So the export collects every such reference, reads it through `IContentService` **under the exporting user's identity**, and rewrites it to a `data:` URI. The printed deck is self-contained and contains only what that user could already read. An asset that cannot be read is left as a link and logged: a missing picture prints broken, exactly as it would on screen — it never fails the export.

## Where the browser runs — and why it isn't in the image

**MeshWeaver ships the capability; the operator supplies the binary.** Nothing about pixel fidelity is baked into the portal image: no NuGet package, no bundled browser download, no post-install step. `HeadlessChromiumPdfRenderer` drives an *already-installed* browser as a plain `Process`.

That is a deliberate answer to "where does it run", and the reason is the shared deployment: `memex` serves every tenant from one image. Adding a browser there would add hundreds of megabytes, a sandbox/security surface, and a new crash mode to **every** portal, in service of a minority of decks. So the cost is opt-in too.

Resolution order, first hit wins:

1. `MarkdownExportConfig.PixelRendering.ExecutablePath`
2. `MESHWEAVER_CHROMIUM_PATH`, `CHROME_BIN`, `PUPPETEER_EXECUTABLE_PATH` — the conventions images that already carry a browser tend to set, so such an image works with no MeshWeaver configuration at all
3. Well-known install locations per platform (`/usr/bin/chromium`, `/usr/bin/google-chrome`, the macOS app bundles, the Windows Program Files paths)

A configured-but-missing `ExecutablePath` reports **unavailable** and logs a warning rather than falling through to auto-detection — an operator who pointed at the wrong path must see that, not silently get a different browser.

```csharp
builder.AddMarkdownExport(cfg =>
{
    cfg.PixelRendering = cfg.PixelRendering with
    {
        ExecutablePath = "/usr/bin/chromium",
        NoSandbox = true,                       // see below
        SettleBudget = TimeSpan.FromSeconds(5),
    };
});
```

`NoSandbox` is **off by default on purpose**: `--no-sandbox` disables Chromium's process sandbox, and that is an operator's security decision, not a default anyone should inherit. Most containers running as a non-root user without `SYS_ADMIN` need it (or a seccomp profile permitting user namespaces) for the browser to start at all.

## Capability, not an error

The export dialog offers the fidelity choice **only when the server has confirmed both** that the node is a Deck *and* that a browser resolves. `ExportDocumentLayoutArea` subscribes `IPixelPdfRenderer.Probe()` — promise-cached in the renderer, so the file-system probe runs once per mesh and replays — and enriches `ExportDocumentControl.PixelFidelityAvailable` when the answer lands. A portal without a browser simply shows no choice, and exports exactly as it did before.

If pixel fidelity is nonetheless requested where it cannot run, the export **fails with an actionable message**. It does not quietly downgrade: a user who asked for pixel fidelity must never receive a content-faithful file believing otherwise.

## Threading — the browser is an ordinary I/O leaf

Both the probe (`File.Exists`) and the print (a subprocess) go through `IIoPool`'s `Process` pool via `InvokeBlocking`, exactly like [`GitCli`](/Doc/Architecture/ControlledIoPooling). Consequences worth naming:

- The work runs **off the hub scheduler** — a burst of exports can never spawn an unbounded pile of browsers; the pool's cap (4 by default) is the governor.
- Unsubscribe or mesh teardown **kills the whole browser tree**, so a pool slot is never held by an orphaned renderer child.
- Each print gets its **own scratch directory** — HTML in, PDF out, and a throwaway browser profile. A shared profile would serialise concurrent prints (Chromium locks it) and leak state between exports.
- A browser that wedges is bounded by `PixelRenderingOptions.Timeout` and killed. That bound exists to stop a hung browser holding a slot — it is not a retry, and raising it is never the fix for a browser that does not finish.

The public surface is `IObservable<T>` throughout; there is no `Task` in a signature and no `Observable.FromAsync` anywhere.

## Testing without a browser

Every decision about *whether a gradient survives* happens in `SlidePrintComposer`, which is pure and synchronous — so the fidelity contract is pinned by ordinary unit tests that need nothing installed (`SlidePrintComposerTests`). The browser tests (`HeadlessChromiumRenderTests`, `DeckPixelExportScriptRelayTest`) ask the renderer's own probe and then assert **whichever contract applies to the machine**: with a browser, a real PDF with one page per slide; without one, a loud, actionable refusal. They are never skipped, and a missing browser can neither hide a regression nor redden a build.

## See also

- [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — the rules the browser leaf follows
- [Slides & Decks](/Doc/GUI/SlidesAndDecks) — `SlideContent.Background`, the manifest, and the live stage
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — exports run as scripts with inputs, progress and output

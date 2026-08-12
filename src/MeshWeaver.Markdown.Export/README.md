# MeshWeaver.Markdown.Export

Server-side export of markdown nodes to PDF and DOCX. No Pandoc, no Node.js; PDF is printed by the
headless Chromium the portal image ships (see [PDF Export](../MeshWeaver.Documentation/Data/Architecture/PixelFaithfulExport.md)).

Pipeline: `Markdig AST` → `Document` model → { print HTML + CSS Paged Media → browser, for PDF |
`DocumentFormat.OpenXml`, for DOCX }.

Features:

- **Embedded layout areas** (`@@("…/area/Foo/…")`) resolved to real document structure — see below.
- Table of contents (built from document heading structure; PDF entries are links, without page numbers — see the doc page).
- Page break rules (before H1, between subtree children, explicit `\newpage` / `<!-- pagebreak -->`).
- Branded cover page, running header, and running footer (with `N / M` page numbers in PDF) resolved from a `CorporateIdentity` mesh node.
- MeshWeaver annotations become native Word comments and tracked changes in DOCX.
- Mermaid / MathJax SVGs captured from the client's already-rendered DOM and embedded as images.

## Embedded layout areas

A document can embed a live view with `@@(…)`. Every export resolves those embeds **server-side**,
because none of the three outputs has a browser session to fill them in later:

```
markdown ──parse──▶ ExportMarkdownPipeline ──find embeds──▶ LayoutAreaResolver.RenderEmbed
                                                                     │
                                                            AreaMarkupRenderer
                                                          (one walk of the control tree)
                                                                     │
                                        ┌────────────────────────────┴────────────────────┐
                                   MarkupNode.Render()                          MarkupToDocument
                                   → HTML (email / print)                       → DocumentElement
                                                                              (print HTML / OpenXml)
```

Two rules keep this from rotting the way it did before:

1. **One pipeline.** `Ast/ExportMarkdownPipeline` is used both to *find* embeds and to *render* the
   document, so whatever the resolver can find, the builder can render. The original defect was two
   pipelines: `DocumentBuilder` built its own without `LayoutAreaMarkdownExtension`, so `@@(…)`
   parsed as a paragraph and every PDF/DOCX printed the embed's **source text**.
2. **One control walk.** `Html/AreaMarkupRenderer` reads the area once into a `MarkupNode` tree;
   HTML and the document model are two serializations of that tree, not two traversals. Teaching
   the renderer a new control reaches every format at once.

Resolution is reactive (it opens each area's synchronization stream and waits for the tree to
settle) while the document build is a synchronous AST walk, so areas are resolved in a **prior
pass** and looked up by key during the walk — the same split already used for client-captured
Mermaid/Math SVGs.

**An area that cannot be resolved becomes a visible, localized notice** (`export.areaUnavailable`),
never a silent gap: a document that looks complete while missing a section its author placed is
worse than one that says so. One unresolvable area never fails the export.

Note on images: the content-fidelity renderers draw **no** images at all (both emit bracketed alt
text — `Pdf/DocumentPrintComposer` and `Docx/DocxDocumentRenderer`), so an area's pictures do not
appear in PDF/DOCX. Links do survive, absolutised against the portal's base URL so they still work
once the file is mailed on. The pixel path *does* render images, and inlines them as `data:` URIs
via `SlideAssetInliner` because its print document is loaded from `file://` under a restrictive CSP.

## Pixel-faithful deck export (opt-in)

`Deck → PDF` can additionally render **pixel-faithfully** (`DocumentExportOptions.Fidelity = Pixel`):
the deck is composed into one self-contained HTML document carrying the *live* stage CSS
(`SlideLayoutAreas.ThemeTokens` + `Pixel/SlidePrint.css`) and printed by a headless browser, so CSS
gradients, background images, raw-HTML slide bodies, CSS layout and transforms survive — none of
which the document model can express.

The `portal-ai` image **ships the browser** (`deploy/base-images/portal-ai`: a Playwright
headless-shell Chromium at `/usr/bin/chromium`, `CHROME_BIN` set) — it has to, because since #1230
the browser prints every PDF, not just this one. Resolution stays overridable:
`HeadlessChromiumPdfRenderer` takes `MarkdownExportConfig.PixelRendering.ExecutablePath`, then
`MESHWEAVER_CHROMIUM_PATH` / `CHROME_BIN` / `PUPPETEER_EXECUTABLE_PATH`, then the platform's usual
locations. Where none exists, pixel fidelity is not offered and a PDF export fails loudly rather
than returning a file that quietly lost its formatting.

Full reference: `Doc/Architecture/PixelFaithfulExport`.

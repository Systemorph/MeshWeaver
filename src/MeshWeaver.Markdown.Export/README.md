# MeshWeaver.Markdown.Export

Server-side export of markdown nodes to PDF and DOCX. The default pipeline is pure C# — no headless browser, no Pandoc, no Node.js required.

Pipeline: `Markdig AST` → `IDocumentVisitor` → { `QuestPDF` for PDF, `DocumentFormat.OpenXml` for DOCX }.

Features:

- **Embedded layout areas** (`@@("…/area/Foo/…")`) resolved to real document structure — see below.
- Table of contents (built from document heading structure).
- Page break rules (before H1, between subtree children, explicit `\newpage` / `<!-- pagebreak -->`).
- Branded cover page, header, and footer resolved from a `CorporateIdentity` mesh node.
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
                                                                                (QuestPDF / OpenXml)
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
text — `Pdf/PdfDocumentRenderer` and `Docx/DocxDocumentRenderer`), so an area's pictures do not
appear in PDF/DOCX. Links do survive, absolutised against the portal's base URL so they still work
once the file is mailed on. The pixel path *does* render images, and inlines them as `data:` URIs
via `SlideAssetInliner` because its print document is loaded from `file://` under a restrictive CSP.

## Pixel-faithful deck export (opt-in)

`Deck → PDF` can additionally render **pixel-faithfully** (`DocumentExportOptions.Fidelity = Pixel`):
the deck is composed into one self-contained HTML document carrying the *live* stage CSS
(`SlideLayoutAreas.ThemeTokens` + `Pixel/SlidePrint.css`) and printed by a headless browser, so CSS
gradients, background images, raw-HTML slide bodies, CSS layout and transforms survive — none of
which the document model can express.

The browser is **not** shipped in the image: `HeadlessChromiumPdfRenderer` drives an already-installed
Chromium/Chrome/Edge found via `MarkdownExportConfig.PixelRendering.ExecutablePath`, then `CHROME_BIN`
/ `PUPPETEER_EXECUTABLE_PATH` / `MESHWEAVER_CHROMIUM_PATH`, then the platform's usual locations. Where
none exists the option is not offered and nothing else changes.

Full reference: `Doc/Architecture/PixelFaithfulExport`.

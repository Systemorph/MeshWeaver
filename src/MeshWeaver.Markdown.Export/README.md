# MeshWeaver.Markdown.Export

Server-side export of markdown nodes to PDF and DOCX. The default pipeline is pure C# — no headless browser, no Pandoc, no Node.js required.

Pipeline: `Markdig AST` → `IDocumentVisitor` → { `QuestPDF` for PDF, `DocumentFormat.OpenXml` for DOCX }.

Features:

- Table of contents (built from document heading structure).
- Page break rules (before H1, between subtree children, explicit `\newpage` / `<!-- pagebreak -->`).
- Branded cover page, header, and footer resolved from a `CorporateIdentity` mesh node.
- MeshWeaver annotations become native Word comments and tracked changes in DOCX.
- Mermaid / MathJax SVGs captured from the client's already-rendered DOM and embedded as images.

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

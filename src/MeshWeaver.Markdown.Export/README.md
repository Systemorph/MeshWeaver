# MeshWeaver.Markdown.Export

Server-side export of markdown nodes to PDF and DOCX. Pure C# pipeline — no headless browser, no Pandoc, no Node.js required.

Pipeline: `Markdig AST` → `IDocumentVisitor` → { `QuestPDF` for PDF, `DocumentFormat.OpenXml` for DOCX }.

Features:

- Table of contents (built from document heading structure).
- Page break rules (before H1, between subtree children, explicit `\newpage` / `<!-- pagebreak -->`).
- Branded cover page, header, and footer resolved from a `CorporateIdentity` mesh node.
- MeshWeaver annotations become native Word comments and tracked changes in DOCX.
- Mermaid / MathJax SVGs captured from the client's already-rendered DOM and embedded as images.

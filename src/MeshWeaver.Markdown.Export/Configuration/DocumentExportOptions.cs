using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// User-selected options for a markdown node export, collected from the dialog.
/// </summary>
public record DocumentExportOptions
{
    /// <summary>Output format.</summary>
    public ExportFormat Format { get; init; } = ExportFormat.Pdf;

    /// <summary>Optional title; falls back to the markdown node name.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Mesh node path to the brand source (typically a <c>CorporateIdentity</c> node,
    /// e.g. <c>Brand/Systemorph</c>). May also point at an Organization node (logo-only)
    /// or a raw content path. Empty = portal default.
    /// </summary>
    public string? BrandNodePath { get; init; }

    /// <summary>Render a branded cover page at the front of the document.</summary>
    public bool CoverPage { get; init; } = true;

    /// <summary>Insert a table of contents after the cover (or at the top if no cover).</summary>
    public bool TableOfContents { get; init; } = true;

    /// <summary>Emit a page break before each <c>#</c> H1 heading (after the first).</summary>
    public bool PageBreakBeforeH1 { get; init; } = true;

    /// <summary>
    /// Emit a page break before each <c>##</c> H2 heading (off by default).
    /// </summary>
    public bool PageBreakBeforeH2 { get; init; }

    /// <summary>
    /// When including children, start each child node on a new page.
    /// Ignored when <see cref="IncludeChildren"/> is false.
    /// </summary>
    public bool PageBreakBetweenChildren { get; init; } = true;

    /// <summary>Include the markdown node's descendants as successive chapters.</summary>
    public bool IncludeChildren { get; init; }

    /// <summary>Max descendant depth when <see cref="IncludeChildren"/> is true (0 = unlimited).</summary>
    public int MaxDepth { get; init; }

    /// <summary>Optional override for footer text; falls back to the brand's footer.</summary>
    public string? FooterOverride { get; init; }

    /// <summary>Optional override for header text; falls back to the brand's header.</summary>
    public string? HeaderOverride { get; init; }

    /// <summary>
    /// Mermaid and MathJax SVG fragments captured from the already-rendered client DOM.
    /// Keys are the zero-based block index for each Mermaid or Math block in source order,
    /// prefixed by block kind (e.g. <c>mermaid:0</c>, <c>math:3</c>).
    /// Unset keys fall back to rendering the original source as a fenced code block.
    /// </summary>
    public ImmutableDictionary<string, string> RenderedSvgs { get; init; }
        = ImmutableDictionary<string, string>.Empty;
}

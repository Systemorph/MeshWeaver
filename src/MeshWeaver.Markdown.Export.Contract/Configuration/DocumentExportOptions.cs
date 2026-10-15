using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Export.Configuration;

/// <summary>
/// User-selected options for a markdown node export, collected from the dialog.
/// </summary>
public record DocumentExportOptions
{
    /// <summary>Output format.</summary>
    public ExportFormat Format { get; init; } = ExportFormat.Pdf;

    /// <summary>
    /// How faithfully the export reproduces the browser's rendering. Defaults to
    /// <see cref="ExportFidelity.Content"/> — the fast, small, text-selectable, browser-free
    /// artifact that suits most documents. <see cref="ExportFidelity.Pixel"/> applies to Deck →
    /// PDF and requires a headless browser on the server.
    /// </summary>
    public ExportFidelity Fidelity { get; init; } = ExportFidelity.Content;

    /// <summary>Optional title; falls back to the markdown node name.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Mesh node path to the brand source (typically a <c>CorporateIdentity</c> node,
    /// e.g. <c>Brand/Systemorph</c>). May also point at an Organization node (logo-only)
    /// or a raw content path. Empty = portal default.
    /// </summary>
    public string? BrandNodePath { get; init; }

    /// <summary>
    /// Absolute portal origin (e.g. <c>https://memex.meshweaver.cloud</c>) that every relative
    /// link and image in an <see cref="ExportFormat.Html"/> export is rewritten against. Empty
    /// falls back to configuration (<c>Portal:BaseUrl</c> → <c>PublicBaseUrl</c> →
    /// <c>Email:WebhookBaseUrl</c>) — the same order the invitation mailer uses, so the two never
    /// disagree about the portal's address.
    ///
    /// <para>This matters only for HTML: a mail client has no page origin, so a relative
    /// <c>/Some/Node</c> href is dead when clicked from an inbox.</para>
    /// </summary>
    public string? BaseUrl { get; init; }

    // 🚨 [JsonIgnore(Never)] on every `= true` flag. The mesh serializer runs
    // DefaultIgnoreCondition = WhenWritingDefault, so a bool set to FALSE equals default(bool)
    // and is dropped from the payload — the receiver then falls back to this initializer and
    // reads it as TRUE. Switching one of these off in the dialog therefore did nothing at all.
    // Never is the codebase's standard remedy (GitHubSyncConfig, NotificationSettings, …).
    /// <summary>Render a branded cover page at the front of the document.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool CoverPage { get; init; } = true;

    /// <summary>Insert a table of contents after the cover (or at the top if no cover).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool TableOfContents { get; init; } = true;

    /// <summary>Emit a page break before each <c>#</c> H1 heading (after the first).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool PageBreakBeforeH1 { get; init; } = true;

    /// <summary>
    /// Emit a page break before each <c>##</c> H2 heading (off by default).
    /// </summary>
    public bool PageBreakBeforeH2 { get; init; }

    /// <summary>
    /// When including children, start each child node on a new page.
    /// Ignored when <see cref="IncludeChildren"/> is false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool PageBreakBetweenChildren { get; init; } = true;

    /// <summary>Include the markdown node's descendants as successive chapters.</summary>
    public bool IncludeChildren { get; init; }

    /// <summary>Max descendant depth when <see cref="IncludeChildren"/> is true (0 = unlimited).</summary>
    public int MaxDepth { get; init; }

    /// <summary>The link depth <see cref="LinkedDocumentsMaxDepth"/> starts at: documents linked directly.</summary>
    public const int DefaultLinkedDocumentsMaxDepth = 1;

    /// <summary>
    /// Append each mesh document the exported content LINKS to as an appendix chapter, so a
    /// recipient who cannot open the portal still gets the content (MeshWeaver.Plugins#3707). The
    /// links are followed to <see cref="LinkedDocumentsMaxDepth"/> levels. Each target is read as
    /// the person exporting. A link to a target that is denied or missing is marked visibly, both
    /// where it stands and in the appendix list, and the target's title is never printed.
    /// </summary>
    [Description("Include linked documents as an appendix")]
    [Translation("de", "Verlinkte Dokumente als Anhang aufnehmen")]
    public bool IncludeLinkedDocuments { get; init; }

    /// <summary>
    /// How many levels of links <see cref="IncludeLinkedDocuments"/> follows: 1 = the documents the
    /// export links to directly, 2 = also the documents THOSE link to, and so on. Values below 1
    /// read as 1. Ignored when <see cref="IncludeLinkedDocuments"/> is false.
    /// </summary>
    // [JsonIgnore(Never)]: the default is not default(int), so a 0 dropped by WhenWritingDefault
    // would come back as the initializer — the same trap the `= true` flags above document.
    [Description("Link levels to follow for the appendix")]
    [Translation("de", "Zu verfolgende Verweisebenen für den Anhang")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int LinkedDocumentsMaxDepth { get; init; } = DefaultLinkedDocumentsMaxDepth;

    /// <summary>
    /// Render pages in landscape (wide) orientation instead of portrait. Off by default
    /// (markdown documents stay portrait A4); the Deck → PDF export turns this on so
    /// 16:9 slides fill the page.
    /// </summary>
    public bool Landscape { get; init; }

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

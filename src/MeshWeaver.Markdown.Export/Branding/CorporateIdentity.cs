using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Markdown.Export.Branding;

/// <summary>
/// Content of a <c>CorporateIdentity</c> mesh node. Carries the visual brand data
/// a document export applies: logo, colors, header/footer text, typography.
/// Resolved by <see cref="BrandingResolver"/> from the selected brand node path.
/// </summary>
public record CorporateIdentity
{
    /// <summary>Unique id (matches the node id).</summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>Display name of the organization, e.g. "Systemorph".</summary>
    public string? Name { get; init; }

    /// <summary>Short tagline appearing on the cover page, e.g. "Data for a thinking enterprise".</summary>
    public string? Tagline { get; init; }

    /// <summary>
    /// Path to the logo. Supports <c>content:...</c> mesh paths (e.g. <c>content:Systemorph/logo.svg</c>),
    /// absolute URLs, or portal-relative paths (e.g. <c>/static/...</c>).
    /// SVG is preferred for crisp scaling in PDF and DOCX.
    /// </summary>
    public string? LogoPath { get; init; }

    /// <summary>Primary brand color as hex (<c>#RRGGBB</c>). Used for headings and cover accents.</summary>
    public string? PrimaryColor { get; init; }

    /// <summary>Accent color as hex. Used for secondary accents and hyperlinks.</summary>
    public string? AccentColor { get; init; }

    /// <summary>Font family for body text, e.g. "Inter", "Segoe UI".</summary>
    public string? FontFamily { get; init; }

    /// <summary>Running header text at the top of each page (excluding cover).</summary>
    public string? HeaderText { get; init; }

    /// <summary>Running footer text at the bottom of each page (excluding cover).</summary>
    public string? FooterText { get; init; }

    /// <summary>Organization website URL, shown on the cover page.</summary>
    public string? Website { get; init; }

    /// <summary>
    /// Optional path to a Word template (.docx) whose header/footer, page setup and
    /// embedded logo are reused by the DOCX export. Supports the same path styles as
    /// <see cref="LogoPath"/> (<c>content:...</c>, <c>/static/storage/content/...</c>).
    /// When set, the first embedded raster image and the major font name from the template
    /// also flow into PDF exports, so the two formats share the same look.
    /// </summary>
    public string? TemplatePath { get; init; }
}

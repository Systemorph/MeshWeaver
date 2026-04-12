namespace MeshWeaver.Markdown.Export.Branding;

/// <summary>
/// Fully resolved branding values ready for the renderer. Produced by <see cref="BrandingResolver"/>
/// after cascading through CorporateIdentity node, Organization node, raw content path, and portal defaults.
/// </summary>
public record BrandingOptions
{
    /// <summary>Organization display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Tagline for cover page; empty when unset.</summary>
    public string Tagline { get; init; } = "";

    /// <summary>
    /// Resolved logo bytes with known mime type. Null when no logo was found.
    /// Renderers embed this directly in the document.
    /// </summary>
    public LogoImage? Logo { get; init; }

    /// <summary>Primary brand color (hex, with leading #). Defaults to a neutral dark.</summary>
    public string PrimaryColor { get; init; } = "#1f2937";

    /// <summary>Accent color (hex, with leading #). Defaults to a neutral mid-tone.</summary>
    public string AccentColor { get; init; } = "#6b7280";

    /// <summary>Body font family. Defaults to a safe sans-serif.</summary>
    public string FontFamily { get; init; } = "Segoe UI";

    /// <summary>Running header text; empty hides the header.</summary>
    public string HeaderText { get; init; } = "";

    /// <summary>Running footer text; empty hides the footer.</summary>
    public string FooterText { get; init; } = "";

    /// <summary>Website URL; empty hides it.</summary>
    public string Website { get; init; } = "";

    /// <summary>Portal default branding used when no brand node is selected or resolvable.</summary>
    public static BrandingOptions Default { get; } = new();
}

/// <summary>
/// Raw logo bytes plus the mime type inferred from the source path.
/// </summary>
public record LogoImage(byte[] Bytes, string MimeType)
{
    /// <summary>True when the logo is an SVG image (handled specially by some renderers).</summary>
    public bool IsSvg => MimeType == "image/svg+xml";
}

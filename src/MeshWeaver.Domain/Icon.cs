namespace MeshWeaver.Domain;


/// <summary>
/// Represents an icon. We link to the fluent icons from Microsoft
/// <see ref="https://fluentui-explorer.azurewebsites.net/icon-explorer"/>
/// </summary>
/// <param name="Provider"></param>
/// <param name="Id"></param>
public record Icon(string Provider, string Id)
{
    /// <summary>The provider key for Fluent UI icons referenced by name (e.g. "Document").</summary>
    public const string FluentProvider = "fluent-ui";

    /// <summary>The provider key for icons referenced by an image URL or data URI held in <see cref="Id"/>.</summary>
    public const string UrlProvider = "url";

    /// <summary>The provider key for icons carried as raw inline <c>&lt;svg&gt;</c> markup in <see cref="Id"/>.</summary>
    public const string InlineSvgProvider = "inline-svg";

    /// <summary>The provider key for icons carried as a literal text glyph (e.g. an emoji) in <see cref="Id"/>.</summary>
    public const string TextProvider = "text";

    /// <summary>
    /// The rendered size of the icon; defaults to <see cref="IconSize.Size24"/>.
    /// </summary>
    public IconSize Size { get; init; } = IconSize.Size24;

    /// <summary>
    /// The icon variant (regular or filled); defaults to <see cref="IconVariant.Regular"/>.
    /// </summary>
    public IconVariant Variant { get; init; } = IconVariant.Regular;

    /// <summary>
    /// TOTAL conversion of the raw string forms an icon field (e.g. <c>MeshNode.Icon</c>) legitimately
    /// holds: inline <c>&lt;svg&gt;</c> markup (<see cref="InlineSvgProvider"/>), an image URL or data
    /// URI (<see cref="UrlProvider"/>), or a Fluent UI icon name such as "Document"
    /// (<see cref="FluentProvider"/>). Any other non-empty value — an emoji or arbitrary text — degrades
    /// to <see cref="TextProvider"/> so binding NEVER throws; null/whitespace parses to null.
    /// </summary>
    /// <param name="value">The raw icon string to classify.</param>
    /// <returns>The parsed icon, or null for null/whitespace input.</returns>
    public static Icon? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return new Icon(InlineSvgProvider, value) { Size = IconSize.Custom };
        if (value.Contains('/') || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return new Icon(UrlProvider, value);
        if (char.IsAsciiLetterUpper(value[0]) && value.All(char.IsAsciiLetter))
            return new Icon(FluentProvider, value);
        return new Icon(TextProvider, value);
    }
}

/// <summary>
/// The size of the icon.
/// </summary>
public enum IconSize
{
    /// <summary>
    /// Icon size 10x10
    /// </summary>
    Size10 = 10,
    /// <summary>
    /// Icon size 12x12
    /// </summary>
    Size12 = 12,
    /// <summary>
    /// Icon size 16x16
    /// </summary>
    Size16 = 16,
    /// <summary>
    /// Icon size 20x20
    /// </summary>
    Size20 = 20,
    /// <summary>
    /// Icon size 24x24
    /// </summary>
    Size24 = 24,
    /// <summary>
    /// Icon size 28x28
    /// </summary>
    Size28 = 28,
    /// <summary>
    /// Icon size 32x32
    /// </summary>
    Size32 = 32,
    /// <summary>
    /// Icon size 48x48
    /// </summary>
    Size48 = 48,

    /// <summary>
    /// Custom size included in the SVG content.
    /// </summary>
    Custom = 0
}

/// <summary>
/// The icon variant.
/// </summary>
public enum IconVariant
{
    /// <summary>
    /// Regular variant of icons.
    /// </summary>
    Regular,
    /// <summary>
    /// Filled variant of icons.
    /// </summary>
    Filled
}

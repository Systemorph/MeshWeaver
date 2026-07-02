using MeshWeaver.Domain;

namespace MeshWeaver.Application.Styles;

/// <summary>
/// Custom SVG icons not available in Fluent UI icon set.
/// </summary>
public static class CustomIcons
{
    /// <summary>The icon-provider key for the custom SVG icon set.</summary>
    public const string Provider = "custom-svg";

    /// <summary>
    /// C# programming language icon showing "C#" text.
    /// </summary>
    public static Icon CSharp() => new Icon(Provider, "CSharp") { Size = IconSize.Size20 };

    /// <summary>Python programming language icon.</summary>
    public static Icon Python() => new Icon(Provider, "Python") { Size = IconSize.Size20 };

    /// <summary>JavaScript programming language icon.</summary>
    public static Icon JavaScript() => new Icon(Provider, "JavaScript") { Size = IconSize.Size20 };

    /// <summary>TypeScript programming language icon.</summary>
    public static Icon TypeScript() => new Icon(Provider, "TypeScript") { Size = IconSize.Size20 };

    /// <summary>
    /// Maps a <c>CodeConfiguration.Language</c>-style identifier (as stored on a Code node) to the
    /// matching language glyph. Unknown / null languages fall back to the C# icon. This is the
    /// single place that decides "which language is this Code node" for icon purposes — used by the
    /// Code node's Overview nav menu so a Python / JS / TS file reads as such at a glance.
    /// </summary>
    public static Icon ForLanguage(string? language) => (language?.Trim().ToLowerInvariant()) switch
    {
        "python" or "py" => Python(),
        "javascript" or "js" or "jsx" or "node" => JavaScript(),
        "typescript" or "ts" or "tsx" => TypeScript(),
        _ => CSharp(),
    };

    /// <summary>
    /// Gets the SVG inner content for a custom icon.
    /// FluentUI expects path/rect/g/circle elements, not full SVG.
    /// </summary>
    public static string? GetSvgContent(string iconId) => iconId switch
    {
        // C# icon: Letter C with hash/sharp symbol
        // Designed for 20x20 viewBox
        "CSharp" => """
            <g fill="currentColor">
                <path d="M4 10c0-3.3 2.2-6 5-6 1.8 0 3.4 1 4.3 2.5l-1.5 1c-.6-1-1.6-1.7-2.8-1.7-1.9 0-3.2 1.8-3.2 4.2s1.3 4.2 3.2 4.2c1.2 0 2.2-.7 2.8-1.7l1.5 1c-.9 1.5-2.5 2.5-4.3 2.5-2.8 0-5-2.7-5-6z"/>
                <path d="M14 7h1.5v6H14V7zm2.5 0H18v6h-1.5V7zm-1.25 1.5v1h3v-1h-3zm0 2v1h3v-1h-3z"/>
            </g>
            """,
        // Simple text glyphs ("Py" / "JS" / "TS") — recognisable at a glance next to a Code node.
        "Python" => """
            <text x="10" y="14" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="700" fill="currentColor">Py</text>
            """,
        "JavaScript" => """
            <text x="10" y="14" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="700" fill="currentColor">JS</text>
            """,
        "TypeScript" => """
            <text x="10" y="14" text-anchor="middle" font-family="sans-serif" font-size="9" font-weight="700" fill="currentColor">TS</text>
            """,
        _ => null
    };
}

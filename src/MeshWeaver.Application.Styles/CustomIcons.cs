using MeshWeaver.Domain;

namespace MeshWeaver.Application.Styles;

/// <summary>
/// Custom SVG icons not available in Fluent UI icon set.
/// </summary>
public static class CustomIcons
{
    public const string Provider = "custom-svg";

    /// <summary>
    /// C# programming language icon showing "C#" text.
    /// </summary>
    public static Icon CSharp() => new Icon(Provider, "CSharp") { Size = IconSize.Size20 };

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
        _ => null
    };
}

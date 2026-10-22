using System.Collections.Immutable;
using MeshWeaver.Domain;

namespace MeshWeaver.Graph;

/// <summary>
/// The symbol of each inbox SOURCE — the medium an item arrived through — as one string an icon
/// control accepts (<see cref="Icon.Parse"/>): a Fluent icon name for the mesh's own sources, and
/// a small inline SVG mark for the outside channels. The grid's first column, the side panel's
/// source list and the tooltip names all read it, so one source looks the same everywhere.
///
/// <para>The marks are neutral, self-drawn glyphs in each channel's recognisable colour (a green
/// speech bubble for iMessage, a green phone-in-bubble for WhatsApp, a purple tile for Teams, a
/// blue / red-flapped envelope for Outlook / Gmail) — no vendor artwork is shipped.</para>
///
/// <para>Keyed by the leg's source id, so a provider module needs no change: a source this table
/// does not know keeps the symbol its own leg declared, and with none gets <see cref="Fallback"/>.</para>
/// </summary>
public static class InboxSourceIcons
{
    /// <summary>The symbol of a source nothing is known about, and whose leg declared none.</summary>
    public const string Fallback = "Circle";

    private const string Open = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" width=\"24\" height=\"24\">";

    private static readonly ImmutableDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // The mesh's own sources — Fluent symbols.
        ["Approvals"] = "ShieldCheckmark",
        ["Notifications"] = "Alert",
        ["Activities"] = "Settings",
        ["Mail"] = "Mail",
        ["Chat"] = "Chat",
        ["Conversations"] = "ChatMultiple",
        // Outside channels — a recognisable colour and shape each.
        ["Outlook"] = Open
            + "<rect x=\"2\" y=\"5\" width=\"20\" height=\"14\" rx=\"3\" fill=\"#0F6CBD\"/>"
            + "<path d=\"M3.5 7.5 12 13.5l8.5-6\" fill=\"none\" stroke=\"#fff\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>",
        ["Gmail"] = Open
            + "<rect x=\"2\" y=\"5\" width=\"20\" height=\"14\" rx=\"3\" fill=\"#fff\" stroke=\"#C5C9CE\"/>"
            + "<path d=\"M3.5 7.5 12 13.5l8.5-6\" fill=\"none\" stroke=\"#EA4335\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>",
        ["iMessage"] = Open
            + "<path d=\"M12 3C6.8 3 2.7 6.5 2.7 10.8c0 2.4 1.3 4.5 3.3 5.9-.1 1.2-.6 2.4-1.5 3.3 1.7-.1 3.2-.8 4.3-1.7.9.3 1.8.4 2.7.4 5.2 0 9.3-3.5 9.3-7.9S17.2 3 12 3z\" fill=\"#34C759\"/></svg>",
        ["WhatsApp"] = Open
            + "<path d=\"M12 2.5a9.5 9.5 0 0 0-8.1 14.4L2.5 21.5l4.7-1.3A9.5 9.5 0 1 0 12 2.5z\" fill=\"#25D366\"/>"
            + "<path d=\"M8.7 7.5c.3-.3.7-.2.9.1l.8 1.6c.1.2.1.5-.1.7l-.6.7c.8 1.5 1.9 2.5 3.4 3.2l.7-.8c.2-.2.5-.3.7-.1l1.6.9c.3.2.4.6.2.9-.6 1-1.7 1.5-2.9 1.2-2.8-.8-4.8-2.9-5.6-5.7-.2-.9.2-1.8.9-2.7z\" fill=\"#fff\"/></svg>",
        ["Teams"] = Open
            + "<rect x=\"2.5\" y=\"3.5\" width=\"19\" height=\"17\" rx=\"4\" fill=\"#5B5FC7\"/>"
            + "<path d=\"M7.5 8.5h9M12 8.5v8\" stroke=\"#fff\" stroke-width=\"2.2\" stroke-linecap=\"round\" fill=\"none\"/></svg>",
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The source ids this table draws a symbol for — what a test pins.</summary>
    public static IEnumerable<string> KnownSources => Known.Keys;

    /// <summary>
    /// The symbol of <paramref name="sourceId"/>: the table's, else <paramref name="declared"/> (what the
    /// source's own leg said), else <see cref="Fallback"/>. Never empty.
    /// </summary>
    /// <param name="sourceId">The leg's source id.</param>
    /// <param name="declared">The symbol the leg declared, or null.</param>
    public static string Resolve(string? sourceId, string? declared)
        => sourceId is { Length: > 0 } && Known.TryGetValue(sourceId, out var icon) ? icon
            : !string.IsNullOrWhiteSpace(declared) ? declared
            : Fallback;

    /// <summary>
    /// The symbol as a text prefix: its glyph when it IS a glyph (an emoji a provider declared), else empty
    /// — an SVG or a Fluent name must never be spliced into a label.
    /// </summary>
    /// <param name="icon">A resolved symbol.</param>
    public static string Glyph(string? icon)
        => Icon.Parse(icon) is { Provider: Icon.TextProvider } text ? text.Id : string.Empty;
}

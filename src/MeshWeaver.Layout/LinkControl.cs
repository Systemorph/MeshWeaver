using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// The href rules every renderer of a <see cref="LinkControl"/> applies, in one place so that Blazor,
/// React and React Native refuse and annotate the same addresses. Pure.
/// </summary>
public static class LinkHrefs
{
    /// <summary>The URL schemes a link may carry. Anything else — <c>javascript:</c>, <c>vbscript:</c>, <c>data:</c>, <c>file:</c> — is refused.</summary>
    public static readonly ImmutableArray<string> AllowedSchemes = ["http", "https", "mailto", "tel"];

    /// <summary>The <c>rel</c> an external link or a link opened in another browsing context carries.</summary>
    public const string ExternalRel = "noopener noreferrer";

    /// <summary>
    /// The scheme of <paramref name="href"/>, lower-cased — what a browser would read: ASCII
    /// whitespace and control characters are dropped first (a browser drops them too, so
    /// <c>"java\tscript:"</c> IS <c>javascript:</c>), and a colon counts only before the first
    /// <c>/</c>, <c>?</c> or <c>#</c>. <c>null</c> for a relative address (<c>/Doc/GUI</c>,
    /// <c>../Sibling</c>, <c>#section</c>, <c>?q=1</c>, <c>//host/path</c>).
    /// </summary>
    /// <param name="href">The address.</param>
    /// <returns>The scheme without its colon, or <c>null</c>.</returns>
    public static string? SchemeOf(string? href)
    {
        if (string.IsNullOrEmpty(href))
            return null;
        var cleaned = new string(href.Where(c => c > ' ' && c != '\u007f').ToArray());
        for (var i = 0; i < cleaned.Length; i++)
        {
            var c = cleaned[i];
            if (c == ':')
                return i == 0 ? null : cleaned[..i].ToLowerInvariant();
            if (c is '/' or '?' or '#')
                return null;
        }
        return null;
    }

    /// <summary>
    /// True when a renderer may put <paramref name="href"/> into an <c>href</c> attribute: a
    /// non-blank relative address, or an absolute one whose scheme is in <see cref="AllowedSchemes"/>.
    /// A refused address is drawn as plain text — never as a link, and never "fixed" into another one.
    /// </summary>
    /// <param name="href">The address.</param>
    /// <returns>True when the address is safe to link to.</returns>
    public static bool IsSafe(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return false;
        var scheme = SchemeOf(href);
        return scheme is null || AllowedSchemes.Contains(scheme);
    }

    /// <summary>
    /// True when <paramref name="href"/> leaves the portal: an absolute <c>http</c>/<c>https</c>
    /// address or a protocol-relative <c>//host</c> one.
    /// </summary>
    /// <param name="href">The address.</param>
    /// <returns>True for an address on another site.</returns>
    public static bool IsExternal(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return false;
        var scheme = SchemeOf(href);
        return scheme is "http" or "https" || (scheme is null && href.TrimStart().StartsWith("//", StringComparison.Ordinal));
    }

    /// <summary>
    /// The <c>rel</c> a renderer gives a link: <see cref="ExternalRel"/> when the address is external
    /// (<see cref="IsExternal"/>) or it opens in another browsing context (a <paramref name="target"/>
    /// other than <c>_self</c>), so the opened page gets no handle on the portal's window and no
    /// referrer; <c>null</c> otherwise.
    /// </summary>
    /// <param name="href">The address.</param>
    /// <param name="target">The link's target, or <c>null</c>.</param>
    /// <returns>The <c>rel</c> value, or <c>null</c>.</returns>
    public static string? RelFor(string? href, string? target) =>
        IsExternal(href)
        || (!string.IsNullOrWhiteSpace(target) && !string.Equals(target, "_self", StringComparison.OrdinalIgnoreCase))
            ? ExternalRel
            : null;
}

/// <summary>
/// An inline text link in body content — "open the pull request", "download the PDF", "connect your
/// account" — instead of a hand-built <c>Controls.Html("&lt;a href=…&gt;")</c>. It reads as a link in
/// running text (compose it in a horizontal <see cref="StackControl"/> beside labels, or as a
/// <see cref="MessageBarControl"/>'s action). For menu and navigation chrome use
/// <see cref="NavLinkControl"/>; for an action that should look like a button use
/// <see cref="ButtonControl.WithNavigateToHref"/>.
///
/// <para>🚨 Every renderer applies <see cref="LinkHrefs"/>: an address whose scheme is not
/// <c>http</c>, <c>https</c>, <c>mailto</c> or <c>tel</c> (a <c>javascript:</c> URL, above all) is
/// drawn as plain text, and an external address or one opened in another tab carries
/// <c>rel="noopener noreferrer"</c>. Both are decided in the renderer, so a BOUND href is held to
/// the same rule as a literal one.</para>
/// </summary>
/// <param name="Text">The link text — a literal or a data-binding pointer. User-facing: pass a localized string.</param>
/// <param name="Href">The address — a literal or a data-binding pointer. A mesh path is written as an absolute path (<c>/Doc/GUI</c>), never <c>@/…</c>.</param>
public record LinkControl(object Text, object Href)
    : UiControl<LinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Where the link opens — <c>_blank</c> for a new tab; the current one when unset. A literal or a binding.</summary>
    public object? Target { get; init; }

    /// <summary>
    /// Makes the link a download: <c>true</c> downloads under the server's file name, a string under
    /// that file name. A literal or a binding; a plain navigation when unset.
    /// </summary>
    public object? Download { get; init; }

    /// <summary>The hover text, rendered as the element's <c>title</c> — a literal or a binding. User-facing: pass a localized string.</summary>
    public object? Tooltip { get; init; }

    /// <summary>Sets where the link opens.</summary>
    /// <param name="target">A browsing-context name such as <c>_blank</c>, or a binding.</param>
    /// <returns>A new <see cref="LinkControl"/> with the target set.</returns>
    public LinkControl WithTarget(object target) => this with { Target = target };

    /// <summary>Opens the link in a new browser tab (<c>target="_blank"</c>, with <c>rel="noopener noreferrer"</c>).</summary>
    /// <returns>A new <see cref="LinkControl"/> that opens in a new tab.</returns>
    public LinkControl WithOpenInNewTab() => this with { Target = "_blank" };

    /// <summary>Makes the link a download (<see cref="Download"/>).</summary>
    /// <param name="download"><c>true</c>, a file name, or a binding.</param>
    /// <returns>A new <see cref="LinkControl"/> that downloads.</returns>
    public LinkControl WithDownload(object download) => this with { Download = download };

    /// <summary>Sets the hover text.</summary>
    /// <param name="tooltip">The localized text, or a binding.</param>
    /// <returns>A new <see cref="LinkControl"/> with the tooltip set.</returns>
    public LinkControl WithTooltip(object tooltip) => this with { Tooltip = tooltip };
}

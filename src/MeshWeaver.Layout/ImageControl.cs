using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.Domain;

namespace MeshWeaver.Layout;

/// <summary>
/// The outline an <see cref="ImageControl"/> is clipped to — an OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>). A renderer that meets a value it does not know
/// applies no outline and says so; it never maps the value onto one of these.
/// </summary>
public static class ImageShape
{
    /// <summary>Square corners — the default.</summary>
    public const string Square = "Square";

    /// <summary>Rounded corners (a tile: about a fifth of the side).</summary>
    public const string Rounded = "Rounded";

    /// <summary>A circle — an avatar.</summary>
    public const string Circle = "Circle";
}

/// <summary>
/// How a picture fills the box of an <see cref="ImageControl"/> — an OPEN vocabulary of string
/// constants, like <see cref="ImageShape"/>. Applies to the picture forms only (a URL, an SVG).
/// </summary>
public static class ImageFit
{
    /// <summary>The whole picture is visible, letter-boxed if its aspect differs — the default, right for icons and logos.</summary>
    public const string Contain = "Contain";

    /// <summary>The picture fills the box and is cropped — right for photos, thumbnails and posters.</summary>
    public const string Cover = "Cover";
}

/// <summary>
/// One picture, glyph or icon, sized and shaped — the platform's control for everything a node icon,
/// a logo, a thumbnail, a poster or an avatar can be. The source is any of the forms a
/// <c>MeshNode.Icon</c> legitimately holds (<see cref="Icon.Parse"/>): an image URL or <c>data:image/…</c>
/// URI (including a node's <c>content:</c> reference, resolved against <see cref="NodePath"/>), raw
/// inline <c>&lt;svg&gt;</c> markup, a text glyph (an emoji), or a Fluent icon name — or a typed
/// <see cref="Icon"/>, or a data-binding pointer to any of those.
///
/// <para>🚨 <b>Rendered safely, by contract.</b> A renderer draws a URL only when
/// <see cref="ImageSources.SafeUrl"/> accepts it (<c>http</c>, <c>https</c>, a relative path, or a
/// <c>data:image/…</c> URI — never <c>javascript:</c> or <c>data:text/html</c>), and draws SVG markup
/// only as an <c>&lt;img&gt;</c> whose source is <see cref="ImageSources.SvgDataUri"/> — never as
/// injected markup, because an image context runs no script and loads nothing external. A source that
/// fails those rules is skipped, and <see cref="Fallback"/> then <see cref="Initials"/> are tried; the
/// one resolution every renderer shares is <see cref="ImageSources.Resolve"/>.</para>
///
/// <para><b>Alt text is required</b> — the second constructor argument — and the caller localizes it.
/// An empty alt marks the image DECORATIVE (it sits beside a label that already says what it is) and
/// is a deliberate statement, not an omission.</para>
/// </summary>
/// <param name="Source">The picture, glyph or icon — any form above, or a data-binding pointer.</param>
/// <param name="Alt">The accessible name, localized by the caller; <c>""</c> for a decorative image. A literal or a binding.</param>
public record ImageControl(object Source, object Alt) : UiControl<ImageControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The default side of the square box, in CSS pixels.</summary>
    public const int DefaultSize = 32;

    /// <summary>The side of the square box — a number of CSS pixels or a CSS length (<see cref="ImageSources.CssLength"/>); <see cref="DefaultSize"/> by default.</summary>
    public object? Size { get; init; } = DefaultSize;

    /// <summary>The box width, overriding <see cref="Size"/> — for a picture that is not square (a poster, a banner).</summary>
    public object? Width { get; init; }

    /// <summary>The box height, overriding <see cref="Size"/>.</summary>
    public object? Height { get; init; }

    /// <summary>One of <see cref="ImageShape"/>; <see cref="ImageShape.Square"/> by default.</summary>
    public object? Shape { get; init; } = ImageShape.Square;

    /// <summary>One of <see cref="ImageFit"/>; <see cref="ImageFit.Contain"/> by default.</summary>
    public object? Fit { get; init; } = ImageFit.Contain;

    /// <summary>The icon to show when <see cref="Source"/> is absent or cannot be drawn safely — any form <see cref="Source"/> takes (typically the NodeType's glyph).</summary>
    public object? Fallback { get; init; }

    /// <summary>The monogram (one to three characters) drawn when neither <see cref="Source"/> nor <see cref="Fallback"/> can be — see <see cref="ImageSources.InitialsOf"/>.</summary>
    public object? Initials { get; init; }

    /// <summary>The node a <c>content:</c> reference in <see cref="Source"/> or <see cref="Fallback"/> belongs to; without it such a reference is not drawn.</summary>
    public object? NodePath { get; init; }

    /// <summary>Sets the accessible name (<c>""</c> for decorative).</summary>
    /// <param name="alt">The localized alt text, or a binding.</param>
    public ImageControl WithAlt(object alt) => this with { Alt = alt };

    /// <summary>Sets the side of the square box.</summary>
    /// <param name="size">CSS pixels, a CSS length, or a binding.</param>
    public ImageControl WithSize(object size) => this with { Size = size };

    /// <summary>Sets the box width (overrides <see cref="Size"/>).</summary>
    /// <param name="width">CSS pixels, a CSS length, or a binding.</param>
    public ImageControl WithWidth(object width) => this with { Width = width };

    /// <summary>Sets the box height (overrides <see cref="Size"/>).</summary>
    /// <param name="height">CSS pixels, a CSS length, or a binding.</param>
    public ImageControl WithHeight(object height) => this with { Height = height };

    /// <summary>Sets the outline — one of <see cref="ImageShape"/>.</summary>
    /// <param name="shape">The shape, or a binding.</param>
    public ImageControl WithShape(object shape) => this with { Shape = shape };

    /// <summary>Sets how a picture fills the box — one of <see cref="ImageFit"/>.</summary>
    /// <param name="fit">The fit, or a binding.</param>
    public ImageControl WithFit(object fit) => this with { Fit = fit };

    /// <summary>Sets the icon drawn when the source cannot be.</summary>
    /// <param name="fallback">Any source form, or a binding.</param>
    public ImageControl WithFallback(object fallback) => this with { Fallback = fallback };

    /// <summary>Sets the monogram drawn when neither source nor fallback can be.</summary>
    /// <param name="initials">One to three characters, or a binding.</param>
    public ImageControl WithInitials(object initials) => this with { Initials = initials };

    /// <summary>Sets the node a <c>content:</c> reference is resolved against.</summary>
    /// <param name="nodePath">The node path, or a binding.</param>
    public ImageControl WithNodePath(object nodePath) => this with { NodePath = nodePath };
}

/// <summary>What <see cref="ImageSources.Resolve"/> decided to draw — an open vocabulary of string constants.</summary>
public static class ImageSourceKind
{
    /// <summary>A safe URL (<see cref="ImageSources.SafeUrl"/>): draw as <c>&lt;img src&gt;</c>.</summary>
    public const string Url = "Url";

    /// <summary>Raw SVG markup: draw ONLY as <c>&lt;img src="<see cref="ImageSources.SvgDataUri"/>(…)"&gt;</c>, never injected.</summary>
    public const string Svg = "Svg";

    /// <summary>A text glyph (an emoji): draw as encoded text.</summary>
    public const string Glyph = "Glyph";

    /// <summary>A named icon (a Fluent icon, or another named provider): draw with the renderer's icon component.</summary>
    public const string Named = "Named";

    /// <summary>A monogram: draw as encoded text on a plate.</summary>
    public const string Initials = "Initials";

    /// <summary>Nothing could be drawn: render nothing.</summary>
    public const string None = "None";
}

/// <summary>
/// The outcome of <see cref="ImageSources.Resolve"/>.
/// </summary>
/// <param name="Kind">One of <see cref="ImageSourceKind"/>.</param>
/// <param name="Value">The URL, the SVG markup, the glyph, the icon name or the initials — per <paramref name="Kind"/>; empty for <see cref="ImageSourceKind.None"/>.</param>
/// <param name="Icon">The icon that won, for <see cref="ImageSourceKind.Named"/> (its provider and variant matter to the renderer).</param>
public readonly record struct ResolvedImage(string Kind, string Value, Icon? Icon = null)
{
    /// <summary>Nothing to draw.</summary>
    public static ResolvedImage Nothing { get; } = new(ImageSourceKind.None, "");
}

/// <summary>
/// The rules every <see cref="ImageControl"/> renderer shares: which URL may be drawn, how SVG
/// markup is drawn, which CSS length is accepted, and which candidate wins. Pure — no IO, no state.
/// </summary>
public static partial class ImageSources
{
    /// <summary>The <c>data:</c> media types an image may carry. Anything else (<c>data:text/html</c>, …) is refused.</summary>
    private static readonly ImmutableHashSet<string> ImageDataMediaTypes = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/avif",
        "image/bmp", "image/x-icon", "image/vnd.microsoft.icon", "image/svg+xml");

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:")]
    private static partial Regex SchemePrefix();

    [GeneratedRegex(@"^(\d+(\.\d+)?)(px|rem|em|%|vw|vh)?$")]
    private static partial Regex CssLengthPattern();

    /// <summary>
    /// <paramref name="url"/> when an <c>&lt;img src&gt;</c> may carry it, otherwise null: an
    /// <c>http</c>/<c>https</c> URL, a relative path (<c>/api/content/…</c>, <c>images/a.png</c>), or a
    /// <c>data:</c> URI of an image media type. Refused: every other scheme (<c>javascript:</c>,
    /// <c>vbscript:</c>, <c>file:</c>, …), a non-image <c>data:</c> URI, and any value carrying a control
    /// character (a browser strips tabs and newlines out of a scheme, so <c>java&#9;script:</c> would
    /// otherwise slip past the scheme check).
    /// </summary>
    /// <param name="url">The candidate URL.</param>
    public static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var value = url.Trim();
        if (value.Any(c => char.IsControl(c)))
            return null;
        if (value.StartsWith('/'))
            return value;
        if (!SchemePrefix().IsMatch(value))
            return value; // a relative path
        var scheme = value[..value.IndexOf(':')].ToLowerInvariant();
        if (scheme is "http" or "https")
            return value;
        if (scheme == "data")
        {
            var rest = value["data:".Length..];
            var end = rest.IndexOfAny([';', ',']);
            var mediaType = (end < 0 ? rest : rest[..end]).Trim().ToLowerInvariant();
            return end >= 0 && ImageDataMediaTypes.Contains(mediaType) ? value : null;
        }
        return null;
    }

    /// <summary>
    /// SVG markup as a <c>data:image/svg+xml</c> URI — the ONLY way an <see cref="ImageControl"/>
    /// renderer draws SVG. In an image context a browser runs no script, fires no event handler and
    /// loads no external resource, so markup that carries a <c>&lt;script&gt;</c> or an
    /// <c>onload</c> is inert. Percent-encoded rather than base64 so the URI stays readable.
    /// </summary>
    /// <param name="svg">The SVG markup.</param>
    public static string SvgDataUri(string svg) =>
        "data:image/svg+xml," + Uri.EscapeDataString(svg);

    /// <summary>
    /// A CSS length a style attribute may carry, or null: a positive number becomes pixels
    /// (<c>48</c> → <c>48px</c>), and a string is accepted only as a number with one of
    /// <c>px rem em % vw vh</c> — so a bound value can never smuggle a second declaration
    /// (<c>10px; background: url(…)</c>) into the style.
    /// </summary>
    /// <param name="value">A number, a string, or a <see cref="JsonElement"/> holding either.</param>
    public static string? CssLength(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement { ValueKind: JsonValueKind.Number } number:
                return Pixels(number.GetDouble());
            case JsonElement { ValueKind: JsonValueKind.String } text:
                return CssLength(text.GetString());
            case JsonElement:
                return null;
            case string text:
                var match = CssLengthPattern().Match(text.Trim());
                if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
                    return null;
                return match.Groups[3].Success ? text.Trim() : Pixels(amount);
            case IConvertible convertible:
                try
                {
                    return Pixels(convertible.ToDouble(CultureInfo.InvariantCulture));
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    private static string? Pixels(double amount) =>
        amount > 0 && double.IsFinite(amount)
            ? amount.ToString("0.##", CultureInfo.InvariantCulture) + "px"
            : null;

    /// <summary>
    /// The monogram of a name — the first letter of its first two words, upper-cased
    /// (<c>"Ada Lovelace"</c> → <c>"AL"</c>, <c>"memex"</c> → <c>"M"</c>), or null for a blank name.
    /// </summary>
    /// <param name="name">The display name.</param>
    public static string? InitialsOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var letters = name
            .Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.FirstOrDefault(char.IsLetterOrDigit))
            .Where(c => c != default)
            .Take(2)
            .Select(c => char.ToUpperInvariant(c));
        var initials = new string(letters.ToArray());
        return initials.Length == 0 ? null : initials;
    }

    /// <summary>
    /// The first of <paramref name="source"/> → <paramref name="fallback"/> → <paramref name="initials"/>
    /// that can be drawn SAFELY, with how to draw it. Total: the answer is
    /// <see cref="ResolvedImage.Nothing"/> when none can, never an exception.
    ///
    /// <para>Per candidate: inline SVG → <see cref="ImageSourceKind.Svg"/>; a URL → <see cref="ImageSourceKind.Url"/>
    /// when <see cref="SafeUrl"/> accepts it, else skipped; a glyph → <see cref="ImageSourceKind.Glyph"/>,
    /// except a value that starts with a URI scheme (<c>javascript:alert(1)</c> parses as text, but it was
    /// meant as a URL and is refused as one); a named icon → <see cref="ImageSourceKind.Named"/> when
    /// <paramref name="canRenderNamed"/> says the renderer has it (an unknown Fluent name falls through).</para>
    /// </summary>
    /// <param name="source">The primary candidate.</param>
    /// <param name="fallback">The candidate tried when the source cannot be drawn.</param>
    /// <param name="initials">The monogram tried last (trimmed; at most three characters are kept).</param>
    /// <param name="canRenderNamed">Whether the renderer can draw a named icon; null accepts every name.</param>
    public static ResolvedImage Resolve(Icon? source, Icon? fallback = null, string? initials = null, Func<Icon, bool>? canRenderNamed = null)
        => Classify(source, canRenderNamed)
           ?? Classify(fallback, canRenderNamed)
           ?? (string.IsNullOrWhiteSpace(initials)
               ? ResolvedImage.Nothing
               : new ResolvedImage(ImageSourceKind.Initials, Truncate(initials.Trim(), 3)));

    private static string Truncate(string value, int max)
    {
        var info = new StringInfo(value);
        return info.LengthInTextElements <= max ? value : info.SubstringByTextElements(0, max);
    }

    /// <summary>
    /// Whether a TEXT value was meant as a URL: it starts with a URI scheme once whitespace and
    /// control characters are dropped — the way a browser reads a scheme, so <c>java&#9;script:</c>
    /// counts as one.
    /// </summary>
    private static bool LooksLikeAUrl(string text) =>
        SchemePrefix().IsMatch(new string(text.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray()));

    private static ResolvedImage? Classify(Icon? icon, Func<Icon, bool>? canRenderNamed)
    {
        if (icon is null || string.IsNullOrWhiteSpace(icon.Id))
            return null;
        return icon.Provider switch
        {
            Icon.InlineSvgProvider => new ResolvedImage(ImageSourceKind.Svg, icon.Id),
            Icon.UrlProvider => SafeUrl(icon.Id) is { } url ? new ResolvedImage(ImageSourceKind.Url, url) : null,
            Icon.TextProvider => LooksLikeAUrl(icon.Id) ? null : new ResolvedImage(ImageSourceKind.Glyph, icon.Id.Trim()),
            _ => canRenderNamed is null || canRenderNamed(icon) ? new ResolvedImage(ImageSourceKind.Named, icon.Id, icon) : null,
        };
    }
}

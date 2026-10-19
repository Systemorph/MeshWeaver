using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// What an <see cref="EmbedControl"/> frames — an OPEN vocabulary of string constants (policy
/// <c>open-vocabulary-string-constants</c>). The kind decides which addresses are accepted and
/// whether the frame is sandboxed; the rules are <see cref="EmbedSources"/>.
/// </summary>
public static class EmbedKind
{
    /// <summary>
    /// A document this portal serves — a PDF from a node's content collection, drawn by the
    /// browser's own reader. Only a RELATIVE address (<c>/api/content/…</c>) is accepted, and the
    /// frame carries no <c>sandbox</c>: browsers refuse to draw a PDF inside a sandboxed frame.
    /// </summary>
    public const string Document = "Document";

    /// <summary>
    /// A page of another site — an external app such as a dashboard. Only an absolute
    /// <c>http</c>/<c>https</c> (or scheme-relative <c>//host</c>) address is accepted, the frame is
    /// ALWAYS sandboxed with <see cref="EmbedControl.Allow"/>'s tokens and nothing else, and it sends
    /// no referrer. A relative address is refused: a same-origin page with <c>allow-scripts</c> and
    /// <c>allow-same-origin</c> could lift its own sandbox.
    /// </summary>
    public const string App = "App";
}

/// <summary>
/// The sandbox permissions an <see cref="EmbedKind.App"/> frame may be granted — the ONLY tokens a
/// renderer writes into its <c>sandbox</c> attribute. Navigating the top window
/// (<c>allow-top-navigation</c>) and lifting the sandbox for opened windows are never grantable.
/// </summary>
public static class EmbedSandbox
{
    /// <summary>The page may run script.</summary>
    public const string Scripts = "allow-scripts";

    /// <summary>The page keeps its own origin — its cookies and storage — so a signed-in app stays signed in.</summary>
    public const string SameOrigin = "allow-same-origin";

    /// <summary>The page may submit forms.</summary>
    public const string Forms = "allow-forms";

    /// <summary>The page may open new windows (still sandboxed).</summary>
    public const string Popups = "allow-popups";

    /// <summary>The page may start downloads.</summary>
    public const string Downloads = "allow-downloads";

    /// <summary>Every token a caller may grant.</summary>
    public static readonly ImmutableArray<string> Grantable = [Scripts, SameOrigin, Forms, Popups, Downloads];

    /// <summary>What an app frame gets when the caller grants nothing explicitly: script and forms, nothing more.</summary>
    public static readonly ImmutableArray<string> Default = [Scripts, Forms];
}

/// <summary>
/// The rules every <see cref="EmbedControl"/> renderer applies, in one place so Blazor and React
/// accept the same addresses and write the same sandbox. Pure.
/// </summary>
public static class EmbedSources
{
    /// <summary>
    /// The address a renderer may put into the frame's <c>src</c>, or <c>null</c> when it must draw
    /// nothing but the title: a <see cref="EmbedKind.Document"/> takes only a relative address of this
    /// portal; an <see cref="EmbedKind.App"/> only an absolute <c>http</c>/<c>https</c> or a
    /// scheme-relative one. An unknown kind accepts nothing. The address is judged as a browser reads
    /// it (<see cref="LinkHrefs.Normalize"/>).
    /// </summary>
    /// <param name="src">The address.</param>
    /// <param name="kind">One of <see cref="EmbedKind"/>.</param>
    /// <returns>The normalized address, or <c>null</c> when refused.</returns>
    public static string? SafeSrc(string? src, string? kind)
    {
        var normalized = LinkHrefs.Normalize(src);
        if (normalized.Length == 0)
            return null;
        var external = LinkHrefs.IsExternal(normalized);
        var scheme = LinkHrefs.SchemeOf(normalized);
        return kind switch
        {
            EmbedKind.Document => scheme is null && !external ? normalized : null,
            EmbedKind.App => external ? normalized : null,
            _ => null,
        };
    }

    /// <summary>
    /// The <c>sandbox</c> attribute value for a frame of <paramref name="kind"/>: <c>null</c> (no
    /// attribute) for a <see cref="EmbedKind.Document"/>, and for every other kind the requested
    /// tokens that are <see cref="EmbedSandbox.Grantable"/>, in that order, de-duplicated — an empty
    /// string (the most restrictive sandbox) when none survives. An unknown token is dropped, never
    /// passed through.
    /// </summary>
    /// <param name="kind">One of <see cref="EmbedKind"/>.</param>
    /// <param name="allow">The requested tokens, space-separated; <c>null</c> for <see cref="EmbedSandbox.Default"/>.</param>
    /// <returns>The attribute value, or <c>null</c> for no sandbox attribute.</returns>
    public static string? SandboxFor(string? kind, string? allow)
    {
        if (kind == EmbedKind.Document)
            return null;
        var requested = allow is null
            ? EmbedSandbox.Default
            : allow.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
        return string.Join(' ', EmbedSandbox.Grantable.Where(requested.Contains));
    }

    /// <summary>The <c>referrerpolicy</c> of a frame: <c>no-referrer</c> for anything but a <see cref="EmbedKind.Document"/> of this portal.</summary>
    /// <param name="kind">One of <see cref="EmbedKind"/>.</param>
    /// <returns>The policy, or <c>null</c> for the browser default.</returns>
    public static string? ReferrerPolicyFor(string? kind) => kind == EmbedKind.Document ? null : "no-referrer";
}

/// <summary>
/// A framed page or document — a PDF this portal serves, or an external app — instead of a
/// hand-built <c>Controls.Html("&lt;iframe …&gt;")</c>. Renderers always load it lazily, give it a
/// theme-aware border, and apply <see cref="EmbedSources"/>: a refused address draws only the title
/// as text, an app frame is always sandboxed and sends no referrer. For a video use
/// <see cref="VideoControl"/>.
///
/// <para>🚨 A site may refuse to be framed (<c>X-Frame-Options</c>, <c>frame-ancestors</c>) and a
/// browser then shows a blank box without telling anyone. Compose a <see cref="LinkControl"/> that
/// opens the address in a new tab next to an app frame, so the page still works.</para>
/// </summary>
/// <param name="Src">The address — a literal or a data-binding pointer.</param>
/// <param name="Title">The frame's accessible name (its <c>title</c>), required — a literal or a binding. User-facing: pass a localized string.</param>
public record EmbedControl(object Src, object Title) : UiControl<EmbedControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The default frame height.</summary>
    public const string DefaultHeight = "600px";

    /// <summary>One of <see cref="EmbedKind"/>; <see cref="EmbedKind.Document"/> by default.</summary>
    public object? Kind { get; init; } = EmbedKind.Document;

    /// <summary>The frame height as a CSS length (<c>"600px"</c>, <c>"78vh"</c>); <see cref="DefaultHeight"/> by default. A literal or a binding.</summary>
    public object? Height { get; init; } = DefaultHeight;

    /// <summary>The tallest the frame may grow, as a CSS length — with a viewport-relative <see cref="Height"/>. Unbounded when unset.</summary>
    public object? MaxHeight { get; init; }

    /// <summary>
    /// The sandbox tokens an <see cref="EmbedKind.App"/> frame is granted, space-separated, from
    /// <see cref="EmbedSandbox"/>; <see cref="EmbedSandbox.Default"/> when unset. Ignored for a
    /// document, which is never sandboxed.
    /// </summary>
    public object? Allow { get; init; }

    /// <summary>Sets the kind (<see cref="Kind"/>).</summary>
    /// <param name="kind">One of <see cref="EmbedKind"/>.</param>
    /// <returns>A new <see cref="EmbedControl"/> with the kind set.</returns>
    public EmbedControl WithKind(object kind) => this with { Kind = kind };

    /// <summary>Sets the frame height (<see cref="Height"/>).</summary>
    /// <param name="height">A CSS length, or a binding.</param>
    /// <returns>A new <see cref="EmbedControl"/> with the height set.</returns>
    public EmbedControl WithHeight(object height) => this with { Height = height };

    /// <summary>Bounds the frame height (<see cref="MaxHeight"/>).</summary>
    /// <param name="maxHeight">A CSS length, or a binding.</param>
    /// <returns>A new <see cref="EmbedControl"/> with the maximum height set.</returns>
    public EmbedControl WithMaxHeight(object maxHeight) => this with { MaxHeight = maxHeight };

    /// <summary>Grants an app frame these sandbox tokens (<see cref="Allow"/>).</summary>
    /// <param name="tokens">Tokens from <see cref="EmbedSandbox"/>.</param>
    /// <returns>A new <see cref="EmbedControl"/> with the grant set.</returns>
    public EmbedControl WithAllow(params string[] tokens) => this with { Allow = string.Join(' ', tokens) };
}

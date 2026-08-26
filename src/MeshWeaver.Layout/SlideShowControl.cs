using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// One pre-rendered slide of a client-side slide show: the slide body as READY HTML (markdown
/// already rendered server-side) plus its background. Carried by
/// <see cref="SlideShowControl.Frames"/> so the presenter view can swap slides locally —
/// no navigation, no server round trip, no per-slide hub activation.
/// </summary>
public record SlideFrame(string Html, string? Background);

/// <summary>
/// Presenter-mode driver for a slide show, in one of two modes:
/// <list type="bullet">
///   <item><b>Frames mode</b> (<see cref="Frames"/> non-empty): the view renders EVERY slide
///     pre-rendered into the page and swaps them client-side on the standard PowerPoint keys
///     (and click-to-advance), updating the address bar via <c>history.replaceState</c> with
///     <see cref="UrlTemplate"/> — the URL stays deep-linkable while navigation never leaves
///     the page. This is the fix for the per-slide full round trip: switching slides used to
///     be a route change that re-resolved and re-rendered the slide server-side (seconds per
///     keypress on a cold hub, and a failure mode when that round trip broke).</item>
///   <item><b>Href mode</b> (no frames — the original behavior): an invisible keyboard driver;
///     each key navigates to the matching href via the framework's standard navigation.</item>
/// </list>
/// Keys in both modes: Right/Down/PageDown/Space/Enter → next · Left/Up/PageUp → prev ·
/// Home → first · End → last · <b>Esc</b> → <see cref="ExitHref"/> (a real navigation in both
/// modes). A <c>null</c> href makes that key a no-op in href mode.
/// </summary>
public record SlideShowControl()
    : UiControl<SlideShowControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Href to navigate to for Home (first slide). Href mode only; null disables the key.</summary>
    public string? FirstHref { get; init; }

    /// <summary>Href to navigate to for Left / Up / PageUp (previous slide). Href mode only; null disables the key (at the start).</summary>
    public string? PreviousHref { get; init; }

    /// <summary>Href to navigate to for Right / Down / PageDown / Space / Enter (next slide). Href mode only; null disables the key (at the end).</summary>
    public string? NextHref { get; init; }

    /// <summary>Href to navigate to for End (last slide). Href mode only; null disables the key.</summary>
    public string? LastHref { get; init; }

    /// <summary>Href to navigate to for Esc (exit the presentation, e.g. back to the deck overview). Null disables the key.</summary>
    public string? ExitHref { get; init; }

    /// <summary>
    /// Every slide of the deck, pre-rendered — non-empty switches the control into frames mode:
    /// the view renders all of them and swaps client-side, so advancing a slide costs no server
    /// round trip at all.
    /// <para>Frames mode is a progressive enhancement of the wire contract: clients that do not
    /// (yet) implement it — the React and React Native drivers are href-only — ignore this
    /// payload, so a producer emitting frames SHOULD keep populating the href fields alongside
    /// them. Frames-capable clients ignore the hrefs for slide swapping (only
    /// <see cref="ExitHref"/> navigates); href-only clients keep presenting exactly as before.
    /// </para>
    /// </summary>
    public ImmutableList<SlideFrame>? Frames { get; init; }

    /// <summary>The slide to show first in frames mode (clamped to the frame count) — typically
    /// parsed off the deep link's <c>?i</c>.</summary>
    public int StartIndex { get; init; }

    /// <summary>
    /// Address-bar template for frames mode, with <c>{0}</c> for the slide index (e.g.
    /// <c>/MyDeck/Present?i={0}</c>). Each client-side swap calls
    /// <c>history.replaceState</c> with it, so the URL stays shareable and reload lands on the
    /// same slide — without a navigation. Null leaves the address bar alone.
    /// </summary>
    public string? UrlTemplate { get; init; }
}

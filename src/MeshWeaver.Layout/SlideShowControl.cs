namespace MeshWeaver.Layout;

/// <summary>
/// Invisible presenter-mode keyboard driver for a slide show. It renders no chrome — it is
/// placed inside a deck's (or a slide's) <c>Present</c> area so that the presentation view can
/// be driven from the keyboard with the standard PowerPoint bindings. Its Blazor view
/// (<c>SlideShowView</c>) attaches a document-level <c>keydown</c> listener and navigates to
/// the matching href via the framework's standard navigation mechanism (no bespoke messages):
/// <list type="bullet">
///   <item><b>Right / Down / PageDown / Space / Enter</b> → <see cref="NextHref"/> (advance)</item>
///   <item><b>Left / Up / PageUp</b> → <see cref="PreviousHref"/> (go back)</item>
///   <item><b>Home</b> → <see cref="FirstHref"/> · <b>End</b> → <see cref="LastHref"/></item>
///   <item><b>Esc</b> → <see cref="ExitHref"/> (leave the presentation)</item>
/// </list>
/// A <c>null</c> href makes that key a no-op — e.g. <see cref="NextHref"/> is null on the last
/// slide, so advancing past the end does nothing.
/// </summary>
public record SlideShowControl()
    : UiControl<SlideShowControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>Href to navigate to for Home (first slide). Null disables the key.</summary>
    public string? FirstHref { get; init; }

    /// <summary>Href to navigate to for Left / Up / PageUp (previous slide). Null disables the key (at the start).</summary>
    public string? PreviousHref { get; init; }

    /// <summary>Href to navigate to for Right / Down / PageDown / Space / Enter (next slide). Null disables the key (at the end).</summary>
    public string? NextHref { get; init; }

    /// <summary>Href to navigate to for End (last slide). Null disables the key.</summary>
    public string? LastHref { get; init; }

    /// <summary>Href to navigate to for Esc (exit the presentation, e.g. back to the deck overview). Null disables the key.</summary>
    public string? ExitHref { get; init; }
}

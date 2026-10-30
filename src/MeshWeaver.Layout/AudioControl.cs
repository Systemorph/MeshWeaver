namespace MeshWeaver.Layout;

/// <summary>
/// The <c>preload</c> hints an <see cref="AudioControl"/> passes to its renderer. Open string
/// constants (the HTML media vocabulary): a renderer that does not know a value omits the hint and
/// leaves the choice to the player.
/// </summary>
public static class AudioPreload
{
    /// <summary>Load nothing until the viewer presses play — the right hint for a list of clips.</summary>
    public const string None = "none";

    /// <summary>Load only the duration and other metadata before playback.</summary>
    public const string Metadata = "metadata";

    /// <summary>The player may load the whole file before playback.</summary>
    public const string Auto = "auto";
}

/// <summary>
/// An in-place audio player: a media source rendered as a native <c>&lt;audio controls&gt;</c>
/// element (Blazor, React) or the platform's native player. It has no frame and no aspect ratio,
/// so it sits in a table cell or a stack row — use <see cref="VideoControl"/> for video.
/// </summary>
/// <param name="Src">The audio source: a media file URL, or a data-binding expression.</param>
public record AudioControl(object Src)
    : UiControl<AudioControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Accessible name of the player (<c>title</c> and <c>aria-label</c>). User-facing: the caller
    /// passes a localized string.
    /// </summary>
    public object? Title { get; init; }

    /// <summary>
    /// How much the player loads before playback — one of <see cref="AudioPreload"/>; defaults to
    /// <see cref="AudioPreload.Metadata"/>.
    /// </summary>
    public object Preload { get; init; } = AudioPreload.Metadata;

    /// <summary>Sets the accessible name.</summary>
    /// <param name="title">The accessible name; a localized string.</param>
    public AudioControl WithTitle(object title) => this with { Title = title };

    /// <summary>Sets the preload hint (an <see cref="AudioPreload"/> value).</summary>
    /// <param name="preload">The preload hint.</param>
    public AudioControl WithPreload(object preload) => this with { Preload = preload };
}

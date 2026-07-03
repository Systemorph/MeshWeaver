namespace MeshWeaver.Layout;

/// <summary>
/// How a <see cref="VideoControl"/> renders its source.
/// </summary>
public enum VideoKind
{
    /// <summary>A directly playable media file rendered as a native <c>&lt;video controls&gt;</c> element.</summary>
    Video,

    /// <summary>An embeddable player page (YouTube/Vimeo embed URL, …) rendered as an <c>&lt;iframe&gt;</c>.</summary>
    Embed
}

/// <summary>
/// Represents a video player control: a media source rendered either as a
/// native HTML5 video element (<see cref="VideoKind.Video"/>) or an embedded
/// player iframe (<see cref="VideoKind.Embed"/>), sized by
/// <see cref="AspectRatio"/>. Used by course pages for lecture recordings and
/// walkthroughs.
/// </summary>
/// <param name="Src">The video source: a media file URL for <see cref="VideoKind.Video"/>, an embed URL for <see cref="VideoKind.Embed"/>.</param>
public record VideoControl(object Src)
    : UiControl<VideoControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Poster image URL shown before playback starts (native video only).
    /// </summary>
    public object? Poster { get; init; }

    /// <summary>
    /// Accessible title of the video (the iframe/video <c>title</c> attribute).
    /// </summary>
    public object? Title { get; init; }

    /// <summary>
    /// How the source is rendered; defaults to <see cref="VideoKind.Video"/>.
    /// </summary>
    public VideoKind Kind { get; init; }

    /// <summary>
    /// CSS aspect ratio of the player box; defaults to <c>"16/9"</c>.
    /// </summary>
    public object AspectRatio { get; init; } = "16/9";

    /// <summary>Sets the poster image URL.</summary>
    /// <param name="poster">The poster image URL.</param>
    public VideoControl WithPoster(object poster) => this with { Poster = poster };

    /// <summary>Sets the accessible title.</summary>
    /// <param name="title">The title.</param>
    public VideoControl WithTitle(object title) => this with { Title = title };

    /// <summary>Sets the render kind (native video vs. embed iframe).</summary>
    /// <param name="kind">The render kind.</param>
    public VideoControl WithKind(VideoKind kind) => this with { Kind = kind };

    /// <summary>Sets the CSS aspect ratio (e.g. <c>"16/9"</c>, <c>"4/3"</c>).</summary>
    /// <param name="aspectRatio">The CSS aspect ratio.</param>
    public VideoControl WithAspectRatio(object aspectRatio) => this with { AspectRatio = aspectRatio };
}

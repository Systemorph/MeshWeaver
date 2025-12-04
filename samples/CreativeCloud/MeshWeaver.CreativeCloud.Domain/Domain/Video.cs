using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a video derived from a story.
/// </summary>
[Display(GroupName = "Content")]
public record Video : INamed, IHasDependencies
{
    /// <summary>
    /// Unique identifier for the video.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Title of the video.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Video description.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Description { get; init; }

    /// <summary>
    /// Transcript of the video content.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Transcript { get; init; }

    /// <summary>
    /// Reference to the story this video is derived from.
    /// </summary>
    [Dimension<Story>]
    public string? StoryId { get; init; }

    /// <summary>
    /// Target platform for the video (e.g., YouTube, Vimeo, TikTok).
    /// </summary>
    public string? Platform { get; init; }

    /// <summary>
    /// URL to the published video.
    /// </summary>
    public string? VideoUrl { get; init; }

    /// <summary>
    /// Duration of the video in seconds.
    /// </summary>
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// Current status of the video.
    /// </summary>
    [Editable(false)]
    public ContentStatus Status { get; init; } = ContentStatus.Draft;

    string INamed.DisplayName => Title;

    /// <summary>
    /// Dependencies for this video (parent story).
    /// </summary>
    public IReadOnlyCollection<string> Dependencies => GetDependencies();

    private List<string> GetDependencies()
    {
        var deps = new List<string>();
        if (!string.IsNullOrEmpty(StoryId))
            deps.Add(DependencyHelper.Story(StoryId));
        return deps;
    }
}

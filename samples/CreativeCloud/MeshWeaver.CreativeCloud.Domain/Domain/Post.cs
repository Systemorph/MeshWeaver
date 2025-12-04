using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a social media post derived from a story.
/// </summary>
[Display(GroupName = "Content")]
public record Post : INamed
{
    /// <summary>
    /// Unique identifier for the post.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Title or hook of the post.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Full content of the post.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Content { get; init; }

    /// <summary>
    /// Reference to the story this post is derived from.
    /// </summary>
    [Dimension<Story>]
    public string? StoryId { get; init; }

    /// <summary>
    /// Target platform for the post (e.g., LinkedIn, Twitter, Facebook).
    /// </summary>
    public string? Platform { get; init; }

    /// <summary>
    /// Content pillar alignment (Tactical, Aspirational, Insightful, Personal).
    /// </summary>
    public string? ContentPillar { get; init; }

    /// <summary>
    /// Current status of the post.
    /// </summary>
    [Editable(false)]
    public ContentStatus Status { get; init; } = ContentStatus.Draft;

    /// <summary>
    /// Scheduled publication date and time.
    /// </summary>
    public DateTime? ScheduledAt { get; init; }

    /// <summary>
    /// Actual publication date and time.
    /// </summary>
    [Editable(false)]
    public DateTime? PublishedAt { get; init; }

    string INamed.DisplayName => Title;
}

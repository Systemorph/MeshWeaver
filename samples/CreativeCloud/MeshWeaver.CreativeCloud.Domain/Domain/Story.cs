using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a story - the main content piece that can be broken down into posts, videos, and events.
/// </summary>
[Display(GroupName = "Content")]
public record Story : INamed
{
    /// <summary>
    /// Unique identifier for the story.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Title of the story.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Full content of the story.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Content { get; init; }

    /// <summary>
    /// Reference to the story arch this story belongs to.
    /// </summary>
    [Dimension<StoryArch>]
    public string? StoryArchId { get; init; }

    /// <summary>
    /// Reference to the author of this story.
    /// </summary>
    [Dimension<Person>]
    public string? AuthorId { get; init; }

    /// <summary>
    /// Current status of the story.
    /// </summary>
    [Editable(false)]
    public ContentStatus Status { get; init; } = ContentStatus.Draft;

    /// <summary>
    /// Date and time when the story was created.
    /// </summary>
    [Editable(false)]
    public DateTime? CreatedAt { get; init; }

    /// <summary>
    /// Date and time when the story was published.
    /// </summary>
    [Editable(false)]
    public DateTime? PublishedAt { get; init; }

    string INamed.DisplayName => Title;
}

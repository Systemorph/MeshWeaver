using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents an event (webinar, conference, workshop, meetup) derived from a story.
/// </summary>
[Display(GroupName = "Content")]
public record Event : INamed, IHasDependencies
{
    /// <summary>
    /// Unique identifier for the event.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// Title of the event.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Description of the event.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Description { get; init; }

    /// <summary>
    /// Reference to the story this event is based on.
    /// </summary>
    [Dimension<Story>]
    public string? StoryId { get; init; }

    /// <summary>
    /// Type of event (e.g., Webinar, Conference, Workshop, Meetup).
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Physical location of the event (if applicable).
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// URL for virtual event access.
    /// </summary>
    public string? VirtualUrl { get; init; }

    /// <summary>
    /// Start date and time of the event.
    /// </summary>
    public DateTime? StartDate { get; init; }

    /// <summary>
    /// End date and time of the event.
    /// </summary>
    public DateTime? EndDate { get; init; }

    /// <summary>
    /// Current status of the event.
    /// </summary>
    [Editable(false)]
    public ContentStatus Status { get; init; } = ContentStatus.Draft;

    string INamed.DisplayName => Title;

    /// <summary>
    /// Dependencies for this event (parent story).
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

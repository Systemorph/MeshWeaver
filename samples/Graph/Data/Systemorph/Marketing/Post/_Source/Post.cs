// <meshweaver>
// Id: Post
// DisplayName: Social Media Post Data Model
// </meshweaver>

/// <summary>
/// Represents a social media post for publishing.
/// </summary>
public record Post
{
    /// <summary>
    /// Unique identifier for the post.
    /// </summary>
    [Key]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Title or headline of the post.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Main content/body of the post.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// URL for the post image or media.
    /// </summary>
    public string? ImageLink { get; init; }

    /// <summary>
    /// Scheduled date and time for publishing.
    /// </summary>
    public DateTime? PublishDate { get; init; }

    /// <summary>
    /// Reference to the Person who publishes this post.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// List of platforms to publish on (LinkedIn, X, Instagram).
    /// </summary>
    public List<string> Platforms { get; init; } = new();

    /// <summary>
    /// Current publication status.
    /// </summary>
    public PostStatus Status { get; init; } = PostStatus.Draft;
}

/// <summary>
/// Publication status of a social media post.
/// </summary>
public enum PostStatus
{
    /// <summary>
    /// Post is being drafted.
    /// </summary>
    Draft,
    /// <summary>
    /// Post is scheduled for future publication.
    /// </summary>
    Scheduled,
    /// <summary>
    /// Post has been published.
    /// </summary>
    Published,
    /// <summary>
    /// Post has been archived.
    /// </summary>
    Archived
}

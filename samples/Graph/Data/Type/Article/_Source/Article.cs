// <meshweaver>
// Id: Article
// DisplayName: Article Data Model
// </meshweaver>

/// <summary>
/// Represents a published article or blog post.
/// </summary>
public record Article
{
    /// <summary>
    /// URL-friendly slug for the article.
    /// </summary>
    [Key]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Title of the article.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Main content in markdown format.
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Publication status (Draft, Published, Archived).
    /// </summary>
    public ArticleStatus Status { get; init; } = ArticleStatus.Draft;

    /// <summary>
    /// Date and time when the article was published.
    /// </summary>
    public DateTime? Published { get; init; }

    /// <summary>
    /// URL for the article thumbnail image.
    /// </summary>
    public string? Thumbnail { get; init; }

    /// <summary>
    /// Number of times the article has been viewed.
    /// </summary>
    public int Views { get; init; }

    /// <summary>
    /// Number of likes or reactions.
    /// </summary>
    public int Likes { get; init; }

    /// <summary>
    /// Number of comments on the article.
    /// </summary>
    public int Comments { get; init; }

    /// <summary>
    /// List of tags for categorization.
    /// </summary>
    public List<string>? Tags { get; init; }

    /// <summary>
    /// List of author identifiers.
    /// </summary>
    public List<string>? Authors { get; init; }
}

/// <summary>
/// Publication status of an article.
/// </summary>
public enum ArticleStatus
{
    /// <summary>
    /// Article is in draft mode.
    /// </summary>
    Draft,
    /// <summary>
    /// Article is publicly published.
    /// </summary>
    Published,
    /// <summary>
    /// Article is archived and no longer visible.
    /// </summary>
    Archived
}

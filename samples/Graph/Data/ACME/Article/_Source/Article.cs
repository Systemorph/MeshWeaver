// <meshweaver>
// Id: Article
// DisplayName: Article Data Model
// </meshweaver>

/// <summary>
/// Represents a published article or blog post.
/// </summary>
public record Article
{
    [Key]
    public string Url { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string Content { get; init; } = string.Empty;
    public ArticleStatus Status { get; init; } = ArticleStatus.Draft;
    public DateTime? Published { get; init; }
    public string? Thumbnail { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public int Comments { get; init; }
    public List<string>? Tags { get; init; }
    public List<string>? Authors { get; init; }
}

public enum ArticleStatus { Draft, Published, Archived }

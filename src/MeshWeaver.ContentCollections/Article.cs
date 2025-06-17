using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;

namespace MeshWeaver.ContentCollections;

public record MarkdownElement
{
    public string Name { get; init; }
    public string Collection { get; init; }
    public string PrerenderedHtml { get; init; }
    public DateTime LastUpdated { get; set; }
    public string Content { get; init; }
    [property: Key] public string Url { get; init; }

    public string Path { get; init; }
    public IReadOnlyList<SubmitCodeRequest> CodeSubmissions { get; set; }
}
public record Article : MarkdownElement
{
    public TimeSpan VideoDuration { get; set; }
    public string VideoUrl { get; set; }
    public string VideoTitle { get; set; }
    public string VideoDescription { get; set; }
    public string VideoTagLine { get; set; }
    public string VideoTranscript { get; set; }
    public string Title { get; set; }
    public bool Pinned { get; init; }
    public string Abstract { get; init; }
    public string Thumbnail { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public int Comments { get; init; }

    public DateTime? Published { get; init; }


    public ArticleStatus Status { get; init; }
    public (ArticleStatus Status, DateTime Timestamp)[] StatusHistory { get; init; } = [];

    public Article SetStatus(ArticleStatus status)
        => this with { Status = status, StatusHistory = StatusHistory.Append((status, DateTime.UtcNow)).ToArray() };


    public Icon Icon { get; init; }
    public string Source { get; init; }

    public List<string> Authors { get; set; } = [];
    public IReadOnlyCollection<Author> AuthorDetails { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public float[] VectorRepresentation { get; set; }
    public string AuthorAvatar { get; set; }

    public string Transcript { get; set; }

}

public enum ArticleStatus
{
    Draft,
    Published,
    Archived
}

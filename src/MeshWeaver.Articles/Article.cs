using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;

namespace MeshWeaver.Articles;


public record Article
{
    public TimeSpan VideoDuration { get; set; }
    public string VideoUrl { get; set; }
    public string VideoTitle { get; set; }
    public string VideoTagLine { get; set; }
    public string VideoTranscript { get; set; }
    public string Extension { get; init; }
    public string Name { get; init; }
    public string Title { get; set; }
    public bool Pinned { get; init; }
    public string Collection { get; init; }
    [property: Key] public string Url { get; init; }
    public string Path { get; init; }
    public string Abstract { get; init; }
    public string Thumbnail { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public int Comments { get; init; }

    public DateTime Published { get; init; }

    public string Content { get; init; }

    public ArticleStatus Status { get; init; }
    public (ArticleStatus Status, DateTime Timestamp)[] StatusHistory { get; init; } = [];

    public Article SetStatus(ArticleStatus status)
        => this with { Status = status, StatusHistory = StatusHistory.Append((status, DateTime.UtcNow)).ToArray() };

    public DateTime Created { get; init; } = DateTime.UtcNow;

    public string PrerenderedHtml { get; init; }
    public Icon Icon { get; init; }
    public string Source { get; init; }

    public List<string> Authors { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public float[] VectorRepresentation { get; set; }
    public string AuthorAvatar { get; set; }
    public DateTime LastUpdated { get; set; }

    public IReadOnlyList<SubmitCodeRequest> CodeSubmissions { get; set; }


}

public enum ArticleStatus
{
    Draft,
    Published,
    Archived
}

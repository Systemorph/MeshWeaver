using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using MeshWeaver.Domain;

namespace MeshWeaver.Articles;


public record Article
{
    public string Extension { get; init; }
    public string Name { get; init; }
    public string Title { get; set; }
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


    public IDictionary<string, string> ToMetadata(string path, string type) =>
        new Dictionary<string, string>()
        {
            { nameof(Url), Url },
            { nameof(Name), Name },
            { nameof(Abstract), Abstract },
            { nameof(Thumbnail), Thumbnail },
            { nameof(Published), Published.ToString(CultureInfo.InvariantCulture) },
            { nameof(Authors), JsonSerializer.Serialize(Authors) },
            { nameof(Tags), JsonSerializer.Serialize(Tags) },
            { nameof(Path), path },
            { nameof(Type), type },
        };



}

public enum ArticleStatus
{
    Draft,
    Published,
    Archived
}

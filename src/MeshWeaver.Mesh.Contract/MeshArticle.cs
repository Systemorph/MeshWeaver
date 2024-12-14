using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;

namespace MeshWeaver.Mesh;


public record MeshArticle
{
    public string Id { get; init; }
    public string Extension { get; init; }
    public string Name { get; init; }
    [property: Key] public string Url { get; init; }
    public string Path { get; init; }
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public int Comments { get; init; }

    public DateTime Published { get; init; }

    public string Content { get; init; }

    public ArticleStatus Status { get; init; }
    public (ArticleStatus Status, DateTime Timestamp)[] StatusHistory { get; init; } = [];

    public MeshArticle SetStatus(ArticleStatus status)
        => this with { Status = status, StatusHistory = StatusHistory.Append((status, DateTime.UtcNow)).ToArray() };

    public DateTime Created { get; init; } = DateTime.UtcNow;

    public string[] Tags { get; init; } = [];

    public string[] Authors { get; init; } = [];
    public string Application { get; init; }

    public IDictionary<string, string> ToMetadata(string path, string type) =>
        new Dictionary<string, string>()
        {
            { nameof(Url), Url },
            { nameof(Name), Name },
            { nameof(Description), Description },
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

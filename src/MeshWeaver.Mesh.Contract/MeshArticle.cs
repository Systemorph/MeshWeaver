using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;

namespace MeshWeaver.Mesh.Contract;

[GenerateSerializer]
public record MeshArticle
{
    public string Name { get; init; }
    [property: Key] public string Url { get; init; }
    public string Path { get; init; }
    public string Description { get; init; }
    string Thumbnail { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public int Comments { get; init; }

    public DateTime Published { get; init; }

    public string Content { get; init; }

    public ArticleStatus Status { get; init; }
    public ImmutableList<(ArticleStatus Status, DateTime Timestamp)> StatusHistory { get; init; } = [];

    public MeshArticle SetStatus(ArticleStatus status)
        => this with { Status = status, StatusHistory = StatusHistory.Add((status, DateTime.UtcNow)) };

    public DateTime Created { get; init; } = DateTime.UtcNow;

    public ImmutableList<string> Tags { get; init; } = [];

    public ImmutableList<string> Authors { get; init; } = [];

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

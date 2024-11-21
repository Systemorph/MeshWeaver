using System.ComponentModel.DataAnnotations;

namespace MeshWeaver.Mesh.Contract;

[GenerateSerializer]
public record MeshArticle(
    [property: Key] string Url,
    string Name,
    string Description,
    string Thumbnail,
    DateTime Published,
    IReadOnlyCollection<string> Authors,
    IReadOnlyCollection<string> Tags
)
{
    public int Views { get; init; }
    public int Likes { get; init; }
    public int Comments { get; init; }

    public string Content { get; init; }

}

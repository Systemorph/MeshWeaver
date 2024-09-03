using MeshWeaver.ShortGuid;

namespace MeshWeaver.Mesh.Contract
{
    [GenerateSerializer]
    public record ArticleEntry(string Name, string Description, string Image, string Url)
    {
        public string Id { get; init; } = Guid.NewGuid().AsString();
    }
}

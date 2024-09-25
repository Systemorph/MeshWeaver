namespace MeshWeaver.Catalog.Domain;

public record MeshNode(string Id, string Name)
{
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public int Followers { get; init; }
}

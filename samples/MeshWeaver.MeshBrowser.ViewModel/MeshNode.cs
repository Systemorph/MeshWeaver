namespace MeshWeaver.MeshBrowser.ViewModel;

public record MeshNode(string Name)
{
    public string Address { get; init; }
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public DateTime Created { get; init; }
    public string[] Tags { get; init; }
}

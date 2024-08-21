using MeshWeaver.Layout;

namespace MeshWeaver.MeshBrowser.ViewModel;

public record MeshDocument(string Name, object Address)
{
    private LayoutAreaReference Area { get; init; }
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public DateOnly Created { get; init; }
    public string[] Tags { get; init; }
}

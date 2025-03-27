namespace MeshWeaver.Articles;

public record FolderInfo(
    string Path,
    string Name,
    int ItemCount
)
{
    public object IsSelected { get; set; }
}

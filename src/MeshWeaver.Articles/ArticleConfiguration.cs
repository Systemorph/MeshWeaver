namespace MeshWeaver.Articles;


public class ContentSourceConfig
{
    public string SourceType { get; set; } = FileSystemContentCollectionFactory.SourceType;
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string BasePath { get; set; }
}

public interface IContentCollectionFactory
{
    ContentCollection Create(ContentSourceConfig config);
}


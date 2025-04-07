using MeshWeaver.Messaging;

namespace MeshWeaver.Articles;

public class FileSystemContentCollectionFactory(IMessageHub hub) : IContentCollectionFactory
{
    public const string SourceType = "FileSystem";
    public ContentCollection Create(ContentSourceConfig config)
    {
        return new FileSystemContentCollection(config, hub);
    }
}

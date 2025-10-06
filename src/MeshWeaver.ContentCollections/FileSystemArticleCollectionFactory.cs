using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public class FileSystemContentCollectionFactory : IContentCollectionFactory
{
    public const string SourceType = "FileSystem";

    public ContentCollection Create(ContentSourceConfig config, IMessageHub hub)
    {
        var provider = new FileSystemStreamProvider(config.BasePath!);
        return new ContentCollection(config, provider, hub);
    }
}

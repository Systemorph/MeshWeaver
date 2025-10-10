using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public class FileSystemContentCollectionFactory : IContentCollectionFactory
{
    public const string SourceType = "FileSystem";

    public async Task<ContentCollection> CreateAsync(ContentCollectionConfig config, IMessageHub hub, CancellationToken cancellationToken = default)
    {
        var provider = new FileSystemStreamProvider(config.BasePath!);
        var collection = new ContentCollection(config, provider, hub);
        await collection.InitializeAsync(cancellationToken);
        return collection;
    }
}

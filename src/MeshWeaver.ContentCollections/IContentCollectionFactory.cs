using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public interface IContentCollectionFactory
{
    Task<ContentCollection> CreateAsync(ContentCollectionConfig config, IMessageHub hub, CancellationToken cancellationToken = default);
}

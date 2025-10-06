using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public interface IContentCollectionFactory
{
    ContentCollection Create(ContentSourceConfig config, IMessageHub hub);
}

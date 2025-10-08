using MeshWeaver.Messaging;

namespace MeshWeaver.ContentCollections;

public interface IContentCollectionFactory
{
    ContentCollection Create(ContentCollectionConfig config, IMessageHub hub);
}

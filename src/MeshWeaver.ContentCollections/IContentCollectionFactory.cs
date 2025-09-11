namespace MeshWeaver.ContentCollections;

public interface IContentCollectionFactory
{
    ContentCollection Create(ContentSourceConfig config);
}

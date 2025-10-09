using System.Collections.Concurrent;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Registry for content collections at hub level.
/// Does NOT delegate to parent - parent delegation is handled by ContentService.
/// </summary>
internal class ContentCollectionRegistry : IContentCollectionRegistry
{
    private readonly ConcurrentDictionary<string, ContentCollectionRegistration> _collections = new();

    public IContentCollectionRegistry WithCollection(string collectionName, ContentCollectionRegistration config)
    {
        _collections[collectionName] = config;
        return this;
    }

    public ContentCollectionRegistration? GetCollection(string collectionName)
    {
        return _collections.TryGetValue(collectionName, out var registration) ? registration : null;
    }

    public IEnumerable<KeyValuePair<string, ContentCollectionRegistration>> Collections
    {
        get
        {
            return _collections.Select(x => new KeyValuePair<string, ContentCollectionRegistration>(x.Key, x.Value));
        }
    }
}

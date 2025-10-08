using System.Collections.Concurrent;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Hierarchical registry for content collections.
/// Follows the same pattern as TypeRegistry, supporting parent-child inheritance.
/// </summary>
internal class ContentCollectionRegistry : IContentCollectionRegistry
{
    private readonly IContentCollectionRegistry? _parent;
    private readonly ConcurrentDictionary<string, ContentCollectionRegistration> _collections = new();

    public ContentCollectionRegistry(IContentCollectionRegistry? parent)
    {
        _parent = parent;
    }

    public IContentCollectionRegistry WithCollection(string collectionName, ContentCollectionRegistration config)
    {
        _collections[collectionName] = config;
        return this;
    }

    public ContentCollectionRegistration? GetCollection(string collectionName)
    {
        // Check local registry first
        if (_collections.TryGetValue(collectionName, out var registration))
            return registration;

        // Fall back to parent registry
        return _parent?.GetCollection(collectionName);
    }

    public IEnumerable<KeyValuePair<string, ContentCollectionRegistration>> Collections
    {
        get
        {
            var ret = _collections.Select(x => new KeyValuePair<string, ContentCollectionRegistration>(x.Key, x.Value));
            if (_parent is not null)
                ret = ret.Concat(_parent.Collections)
                    .DistinctBy(x => x.Key);
            return ret;
        }
    }
}

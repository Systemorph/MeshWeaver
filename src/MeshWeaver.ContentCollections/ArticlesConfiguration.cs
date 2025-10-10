namespace MeshWeaver.ContentCollections;

public record ArticlesConfiguration
{
    internal IReadOnlyCollection<string> Collections { get; init; } = [];
    internal IReadOnlyCollection<ContentCollectionConfig> CollectionConfigurations { get; init; } = [];
}

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a registered data source provider type (FileSystem, Postgres, Cosmos, etc.).
/// Providers define what configuration fields are needed and how to create storage adapters.
/// </summary>
public record MeshDataSourceProviderDefinition(
    string ProviderType,
    string DisplayName,
    string? Icon = null,
    string? Description = null);

/// <summary>
/// Collection of registered provider definitions, stored via config.Set().
/// </summary>
public record MeshDataSourceProviderRegistry(
    IReadOnlyList<MeshDataSourceProviderDefinition> Providers)
{
    public MeshDataSourceProviderRegistry Add(MeshDataSourceProviderDefinition provider)
        => new(Providers.Append(provider).ToList());
}

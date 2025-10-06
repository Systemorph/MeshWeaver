namespace MeshWeaver.ContentCollections;

/// <summary>
/// Configuration for stream providers from appsettings
/// </summary>
public record StreamProviderConfiguration
{
    /// <summary>
    /// Logical name for the stream provider (e.g., "LocalFiles", "AzureArticles")
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Provider type (e.g., "FileSystem", "AzureBlob")
    /// </summary>
    public string ProviderType { get; init; } = string.Empty;

    /// <summary>
    /// Collections that use this stream provider (optional, defaults to collection with same name as provider)
    /// </summary>
    public List<string> Collections { get; init; } = new();

    /// <summary>
    /// Provider-specific configuration (e.g., base path, connection string)
    /// </summary>
    public Dictionary<string, string> Settings { get; init; } = new();
}

/// <summary>
/// Configuration for multiple stream providers
/// </summary>
public record StreamProvidersConfiguration
{
    public List<StreamProviderConfiguration> Providers { get; init; } = new();
}

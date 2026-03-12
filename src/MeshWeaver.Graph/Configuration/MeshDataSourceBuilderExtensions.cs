using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for registering data sources on the MeshBuilder.
/// </summary>
public static class MeshDataSourceBuilderExtensions
{
    /// <summary>
    /// Registers a named data source that will appear as a MeshNode of type "MeshDataSource"
    /// under the _sources namespace.
    /// </summary>
    public static TBuilder AddDataSource<TBuilder>(
        this TBuilder builder,
        string name,
        string displayName,
        MeshDataSourceConfiguration configuration) where TBuilder : MeshBuilder
    {
        var node = new MeshNode(name, MeshDataSourceNodeType.SourcesNamespace)
        {
            NodeType = MeshDataSourceNodeType.NodeType,
            Name = displayName,
            Icon = GetIconForProviderType(configuration.ProviderType),
            Content = configuration
        };

        builder.AddMeshNodes(node);
        return builder;
    }

    /// <summary>
    /// Registers a FileSystem data source from a base path.
    /// </summary>
    public static TBuilder AddFileSystemDataSource<TBuilder>(
        this TBuilder builder,
        string name,
        string displayName,
        string basePath,
        string? description = null) where TBuilder : MeshBuilder
    {
        var resolvedPath = Path.IsPathRooted(basePath)
            ? basePath
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), basePath));

        var config = new MeshDataSourceConfiguration
        {
            ProviderType = "FileSystem",
            Enabled = true,
            IncludeInSearch = true,
            Description = description,
            StorageConfig = new GraphStorageConfig
            {
                Type = "FileSystem",
                BasePath = resolvedPath
            }
        };
        return builder.AddDataSource(name, displayName, config);
    }

    /// <summary>
    /// Registers a data source provider definition on the mesh hub configuration.
    /// </summary>
    public static TBuilder AddMeshDataSourceProvider<TBuilder>(
        this TBuilder builder,
        MeshDataSourceProviderDefinition provider) where TBuilder : MeshBuilder
    {
        builder.ConfigureHub(config =>
        {
            var registry = config.Get<MeshDataSourceProviderRegistry>()
                ?? new MeshDataSourceProviderRegistry([]);
            return config.Set(registry.Add(provider));
        });
        return builder;
    }

    private static string GetIconForProviderType(string providerType) => providerType switch
    {
        "FileSystem" => "/static/NodeTypeIcons/folder.svg",
        "Cosmos" => "/static/NodeTypeIcons/database.svg",
        "PostgreSql" => "/static/NodeTypeIcons/database.svg",
        "AzureBlob" => "/static/NodeTypeIcons/database.svg",
        "Agents" => "/static/NodeTypeIcons/bot.svg",
        "Documentation" => "/static/NodeTypeIcons/document.svg",
        _ => "/static/NodeTypeIcons/database.svg"
    };
}

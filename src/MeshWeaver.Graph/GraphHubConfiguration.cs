using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Options for configuring graph content collections.
/// </summary>
public record GraphContentOptions
{
    /// <summary>
    /// Base path for file system content storage (used in development).
    /// </summary>
    public string? FileSystemBasePath { get; init; }

    /// <summary>
    /// Azure Blob container name (used in production).
    /// </summary>
    public string? AzureBlobContainer { get; init; }

    /// <summary>
    /// Azure Blob client name (default: "default").
    /// </summary>
    public string AzureBlobClientName { get; init; } = "default";

    /// <summary>
    /// Whether to use Azure Blob storage instead of file system.
    /// </summary>
    public bool UseAzureBlob { get; init; }
}

/// <summary>
/// Hub configuration methods for the Graph application hierarchy.
/// </summary>
public static class GraphHubConfiguration
{
    /// <summary>
    /// Configures the top-level Graph hub that lists all organizations.
    /// Address: @graph
    /// </summary>
    public static MessageHubConfiguration ConfigureGraphHub(this MessageHubConfiguration config)
    {
        return config
            .AddGraph()
            .AddContentCollections()
            .WithTypes(typeof(Organization), typeof(Vertex), typeof(VertexComment), typeof(GraphNamespace));
    }

    /// <summary>
    /// Configures an organization hub that lists all namespaces for the org.
    /// Address: @graph/{org}
    /// </summary>
    public static MessageHubConfiguration ConfigureOrganizationHub(
        this MessageHubConfiguration config,
        string organization)
    {
        return config
            .AddGraph()
            .AddContentCollections()
            .WithTypes(typeof(Organization), typeof(GraphNamespace));
    }

    /// <summary>
    /// Configures a namespace hub with search functionality for vertices.
    /// Address: @graph/{org}/{namespace}
    /// </summary>
    public static MessageHubConfiguration ConfigureNamespaceHub(
        this MessageHubConfiguration config,
        string organization,
        string @namespace)
    {
        return config
            .AddGraph()
            .AddContentCollections()
            .WithTypes(typeof(Vertex), typeof(VertexTypeConfig));
    }

    /// <summary>
    /// Configures a namespace hub with content collection support.
    /// Address: @graph/{org}/{namespace}
    /// </summary>
    public static MessageHubConfiguration ConfigureNamespaceHub(
        this MessageHubConfiguration config,
        string organization,
        string @namespace,
        GraphContentOptions contentOptions)
    {
        var result = config
            .AddGraph()
            .WithTypes(typeof(Vertex), typeof(VertexTypeConfig));

        // Add content collection for this namespace
        var collectionName = $"{organization}/{@namespace}";

        if (contentOptions.UseAzureBlob && !string.IsNullOrEmpty(contentOptions.AzureBlobContainer))
        {
            // Azure Blob storage configuration - handled by MeshWeaver.Hosting.AzureBlob
            result = result.AddContentCollection(_ => new ContentCollectionConfig
            {
                Name = collectionName,
                SourceType = "AzureBlob",
                Settings = new Dictionary<string, string>
                {
                    ["ContainerName"] = contentOptions.AzureBlobContainer,
                    ["ClientName"] = contentOptions.AzureBlobClientName,
                    ["Prefix"] = $"{organization}/{@namespace}/"
                }
            });
        }
        else if (!string.IsNullOrEmpty(contentOptions.FileSystemBasePath))
        {
            // File system storage
            var contentPath = Path.Combine(contentOptions.FileSystemBasePath, @namespace);
            result = result.AddFileSystemContentCollection(collectionName, _ => contentPath);
        }

        return result;
    }

    /// <summary>
    /// Configures a vertex hub with all satellite data (comments, dependencies).
    /// Address: @graph/{org}/{namespace}/{type}/{id}
    /// </summary>
    public static MessageHubConfiguration ConfigureVertexHub(
        this MessageHubConfiguration config,
        string organization,
        string @namespace,
        string type,
        string id)
    {
        return config
            .AddGraph()
            .AddContentCollections()
            .WithTypes(typeof(Vertex), typeof(VertexComment));
    }
}

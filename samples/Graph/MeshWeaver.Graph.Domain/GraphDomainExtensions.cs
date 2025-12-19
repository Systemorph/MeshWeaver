using MeshWeaver.ContentCollections;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Domain.Models;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Domain;

/// <summary>
/// Extensions for configuring the Graph domain hubs.
/// Uses the generic MeshHubBuilder from MeshWeaver.Graph with domain-specific data types.
///
/// Path structure (simplified - no type markers between segments):
/// - graph → Root hub (lists organizations and users)
/// - graph/{personId} → Person hub (e.g., graph/alice)
/// - graph/{orgId} → Organization hub (e.g., graph/org3)
/// - graph/{orgId}/{projectId} → Project hub (e.g., graph/org3/project1)
/// - graph/{orgId}/{projectId}/{storyId} → Story hub (e.g., graph/org3/project1/story2)
/// </summary>
public static class GraphDomainExtensions
{
    /// <summary>
    /// Extensions for MeshWeaver.Graph
    /// </summary>
    /// <param name="configuration">The hub configuration.</param>
    extension(MessageHubConfiguration configuration)
    {
        /// <summary>
        /// Configures the root graph hub at graph address.
        /// Lists all organizations (direct children of graph/).
        /// Data is loaded automatically from IPersistenceService.
        /// Registers the 'persons' content collection for avatar images.
        /// </summary>
        public MessageHubConfiguration ConfigureGraphHub()
            => configuration
                .ConfigureMeshHub()
                .Build()
                .AddDynamicNodeTypeAreas()
                .AddFileSystemContentCollection("persons", sp => GetContentPath(sp, "persons"))
                .AddFileSystemContentCollection("logos", sp => GetContentPath(sp, "logos"));

        /// <summary>
        /// Gets the content path for a collection from IConfiguration.
        /// Configuration key: Graph:{collectionName}Path (e.g., Graph:personsPath)
        /// Falls back to Data/{collectionName} relative to current directory.
        /// </summary>
        private static string GetContentPath(IServiceProvider sp, string collectionName)
        {
            // Get path from IConfiguration (e.g., Graph:personsPath)
            var config = sp.GetRequiredService<IConfiguration>();
            var configPath = config.GetSection("Graph")[collectionName + "Path"];
            if (!string.IsNullOrEmpty(configPath))
                return configPath;

            // Default fallback - not recommended for production
            return Path.Combine(Directory.GetCurrentDirectory(), "Data", collectionName);
        }

        /// <summary>
        /// Configures a person hub at graph/{personId}.
        /// Shows person profile with avatar.
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigurePersonHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Person>()
                .Build()
                .AddDynamicNodeTypeAreas();

        /// <summary>
        /// Configures an organization hub at graph/{orgId}.
        /// Lists all projects for this organization (direct children).
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureOrganizationHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Organization>()
                .Build()
                .AddDynamicNodeTypeAreas();

        /// <summary>
        /// Configures a project hub at graph/{orgId}/{projectId}.
        /// Lists all stories for this project (direct children).
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureProjectHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Project>()
                .Build()
                .AddDynamicNodeTypeAreas();

        /// <summary>
        /// Configures a story hub at graph/{orgId}/{projectId}/{storyId}.
        /// Shows the story details. Mesh navigation is enabled (will show empty children list).
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureStoryHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Story>()
                .Build()
                .AddDynamicNodeTypeAreas();

        /// <summary>
        /// Configures an article hub.
        /// Shows the article details with YAML frontmatter support.
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureArticleHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Article>()
                .Build()
                .AddDynamicNodeTypeAreas();
    }
}

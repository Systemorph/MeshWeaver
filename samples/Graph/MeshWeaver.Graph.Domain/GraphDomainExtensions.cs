using MeshWeaver.Graph.Domain.Models;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Domain;

/// <summary>
/// Extensions for configuring the Graph domain hubs.
/// Uses the generic MeshHubBuilder from MeshWeaver.Graph with domain-specific data types.
///
/// Path structure (simplified - no type markers between segments):
/// - graph → Root hub (lists organizations)
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
        /// </summary>
        public MessageHubConfiguration ConfigureGraphHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Organization>()
                .Build();

        /// <summary>
        /// Configures an organization hub at graph/{orgId}.
        /// Lists all projects for this organization (direct children).
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureOrganizationHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataTypes(typeof(Organization), typeof(Project))
                .Build();

        /// <summary>
        /// Configures a project hub at graph/{orgId}/{projectId}.
        /// Lists all stories for this project (direct children).
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureProjectHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataTypes(typeof(Project), typeof(Story))
                .Build();

        /// <summary>
        /// Configures a story hub at graph/{orgId}/{projectId}/{storyId}.
        /// Shows the story details (no mesh navigation since stories are leaf nodes).
        /// Data is loaded automatically from IPersistenceService.
        /// </summary>
        public MessageHubConfiguration ConfigureStoryHub()
            => configuration
                .ConfigureMeshHub()
                .WithDataType<Story>()
                .WithMeshNavigation(false) // Stories are leaf nodes, no navigation needed
                .Build();
    }
}

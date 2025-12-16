using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Domain.Models;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: MeshWeaver.Graph.Domain.GraphDomain]

namespace MeshWeaver.Graph.Domain;

/// <summary>
/// Registers the Graph domain node templates (factories) for hub configuration.
/// Actual node instances come from the file system via IPersistenceService.
///
/// Path structure (simplified - no type markers between segments):
/// - graph → Root hub (lists organizations and users)
/// - graph/{personId} → Person hub (e.g., graph/alice)
/// - graph/{orgId} → Organization hub (e.g., graph/org3)
/// - graph/{orgId}/{projectId} → Project hub (e.g., graph/org3/project1)
/// - graph/{orgId}/{projectId}/{storyId} → Story hub (e.g., graph/org3/project1/story2)
/// </summary>
public class GraphDomainAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Organization node type identifier.
    /// </summary>
    public const string OrgType = "org";

    /// <summary>
    /// Project node type identifier.
    /// </summary>
    public const string ProjectType = "project";

    /// <summary>
    /// Story node type identifier.
    /// </summary>
    public const string StoryType = "story";

    /// <summary>
    /// Article node type identifier.
    /// </summary>
    public const string ArticleType = "article";

    /// <summary>
    /// Person node type identifier.
    /// </summary>
    public const string PersonType = "person";

    /// <summary>
    /// Graph root address type.
    /// </summary>
    public const string GraphType = "graph";

    /// <summary>
    /// Root graph address.
    /// </summary>
    public static readonly Address GraphAddress = new(GraphType);

    /// <summary>
    /// Gets the mesh nodes for the Graph domain.
    /// Only the root "graph" node is registered here.
    /// Child nodes (organizations, projects, stories) are loaded from the file system
    /// and their HubConfiguration is determined by NodeType via NodeTypeConfigurations.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        // Root graph hub - matches "graph" exactly
        new(GraphType)
        {
            Name = "Graph",
            NodeType = GraphType,
            Description = "Root of hierarchical graph",
            IconName = "Diagram",
            DisplayOrder = 1,
            HubConfiguration = GraphDomainExtensions.ConfigureGraphHub
        }
    ];

    /// <summary>
    /// Gets the node type configurations that map NodeType strings to their DataType and HubConfiguration.
    /// Used to determine how to serialize/deserialize Content and configure hubs for each node type.
    /// </summary>
    public override IEnumerable<NodeTypeConfiguration> NodeTypeConfigurations =>
    [
        new NodeTypeConfiguration
        {
            NodeType = GraphType,
            DataType = typeof(object), // Graph root has no specific content type
            HubConfiguration = GraphDomainExtensions.ConfigureGraphHub,
            DisplayName = "Graph",
            IconName = "Diagram",
            Description = "Root of hierarchical graph",
            DisplayOrder = 1
        },
        new NodeTypeConfiguration
        {
            NodeType = OrgType,
            DataType = typeof(Organization),
            HubConfiguration = GraphDomainExtensions.ConfigureOrganizationHub,
            DisplayName = "Organization",
            IconName = "Building",
            Description = "An organization containing projects",
            DisplayOrder = 10
        },
        new NodeTypeConfiguration
        {
            NodeType = ProjectType,
            DataType = typeof(Project),
            HubConfiguration = GraphDomainExtensions.ConfigureProjectHub,
            DisplayName = "Project",
            IconName = "Folder",
            Description = "A project containing stories",
            DisplayOrder = 20
        },
        new NodeTypeConfiguration
        {
            NodeType = StoryType,
            DataType = typeof(Story),
            HubConfiguration = GraphDomainExtensions.ConfigureStoryHub,
            DisplayName = "Story",
            IconName = "Document",
            Description = "A user story or task",
            DisplayOrder = 30
        },
        new NodeTypeConfiguration
        {
            NodeType = ArticleType,
            DataType = typeof(Article),
            HubConfiguration = GraphDomainExtensions.ConfigureArticleHub,
            DisplayName = "Article",
            IconName = "DocumentText",
            Description = "A content article with YAML frontmatter",
            DisplayOrder = 35
        },
        new NodeTypeConfiguration
        {
            NodeType = PersonType,
            DataType = typeof(Person),
            HubConfiguration = GraphDomainExtensions.ConfigurePersonHub,
            DisplayName = "Person",
            IconName = "Person",
            Description = "A person with profile and avatar",
            DisplayOrder = 5
        }
    ];
}

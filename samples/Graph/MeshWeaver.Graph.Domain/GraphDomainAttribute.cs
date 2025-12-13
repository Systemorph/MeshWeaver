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
/// - graph → Root hub (lists organizations)
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
    /// Graph root address type.
    /// </summary>
    public const string GraphType = "graph";

    /// <summary>
    /// Root graph address.
    /// </summary>
    public static readonly Address GraphAddress = new(GraphType);

    /// <summary>
    /// Gets the mesh node templates for the Graph domain.
    /// These are factory templates that define how to configure hubs when paths match.
    /// Actual node data comes from the file system.
    /// Template paths use format: $template/{prefix}/{addressSegments}
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        // Root graph hub template - matches "graph" exactly (1 segment)
        new(GraphType)
        {
            Name = "Graph",
            Description = "Root of hierarchical graph",
            IconName = "Diagram",
            DisplayOrder = 1,
            HubConfiguration = GraphDomainExtensions.ConfigureGraphHub,
            AutocompleteAddress = _ => GraphAddress
        },

        // Organization template - matches graph/{orgId} (2 segments)
        // e.g., graph/org3
        new("$template/graph/2")
        {
            Name = "Organization",
            NodeType = OrgType,
            Description = "Organization template",
            IconName = "Building",
            DisplayOrder = 10,
            AddressSegments = 2,
            HubConfiguration = GraphDomainExtensions.ConfigureOrganizationHub,
            AutocompleteAddress = _ => GraphAddress
        },

        // Project template - matches graph/{orgId}/{projectId} (3 segments)
        // e.g., graph/org3/project1
        new("$template/graph/3")
        {
            Name = "Project",
            NodeType = ProjectType,
            Description = "Project template",
            IconName = "Folder",
            DisplayOrder = 20,
            AddressSegments = 3,
            HubConfiguration = GraphDomainExtensions.ConfigureProjectHub,
            AutocompleteAddress = ctx => ctx?.Address != null && ctx.Address.Segments.Length >= 2
                ? new Address(string.Join("/", ctx.Address.Segments.Take(2)))
                : GraphAddress
        },

        // Story template - matches graph/{orgId}/{projectId}/{storyId} (4 segments)
        // e.g., graph/org3/project1/story2
        new("$template/graph/4")
        {
            Name = "Story",
            NodeType = StoryType,
            Description = "Story template",
            IconName = "Document",
            DisplayOrder = 30,
            AddressSegments = 4,
            HubConfiguration = GraphDomainExtensions.ConfigureStoryHub,
            AutocompleteAddress = ctx =>
            {
                if (ctx?.Address == null || ctx.Address.Segments.Length < 3)
                    return GraphAddress;
                return new Address(string.Join("/", ctx.Address.Segments.Take(3)));
            }
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
        }
    ];
}

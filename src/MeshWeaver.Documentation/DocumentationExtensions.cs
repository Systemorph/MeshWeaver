using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Documentation;

public static class DocumentationExtensions
{
    /// <summary>
    /// Registers MeshWeaver platform documentation as a read-only
    /// embedded-resource partition under the <c>Doc</c> namespace and
    /// serves documentation content (icons, images) as embedded
    /// resources via <c>DocContent</c>.
    ///
    /// <para>The partition is provisioned by
    /// <see cref="EmbeddedResourceStorageAdapter"/>; it does NOT go
    /// through <c>IStaticNodeProvider</c>. The legacy provider path
    /// caused a cyclic-DI stack overflow because
    /// <see cref="MeshDataSource.WithMeshNodes"/> enumerated providers
    /// during hub construction, and <c>DocumentationNodeProvider</c>'s
    /// dependency on <c>IMessageHub</c> re-entered the singleton
    /// factory.</para>
    /// </summary>
    public static TBuilder AddDocumentation<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddEmbeddedResourcePartition(
            DocumentationNodeProvider.RootNamespace,
            typeof(DocumentationExtensions).Assembly,
            "MeshWeaver.Documentation.Data",
            "Built-in MeshWeaver platform documentation");

        // Surface the partition in Global Settings.
        // DefaultActivityParentPath = "{viewer}" — every script run kicked off
        // from a Doc Code node lands in the calling user's home rather than in
        // the docs partition itself. Per the Activity Control Plane pattern,
        // the "{viewer}" sentinel is resolved per-call by CodeNodeType from
        // the AccessContext.
        builder.AddMeshNodes(new MeshNode("Documentation", "Admin/Partition")
        {
            NodeType = "Partition",
            Name = "MeshWeaver Documentation",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = DocumentationNodeProvider.RootNamespace,
                DataSource = "EmbeddedResource",
                Description = "Built-in MeshWeaver platform documentation",
                Versioned = false,
                DefaultActivityParentPath = "{viewer}"
            }
        });

        builder.ConfigureHub(config => config
            .AddEmbeddedResourceContentCollection(
                "DocContent",
                typeof(DocumentationExtensions).Assembly,
                "Content"));

        // Doc namespace caps: read-only docs (no Create/Update/Delete) but
        // discussion + commenting allowed. Previously lived on the legacy
        // DocumentationNodeProvider but that path caused a DI cycle when
        // wired via IStaticNodeProvider, so seed the policy + Public Viewer
        // grant directly via AddMeshNodes — SecurityService now picks both
        // up from MeshConfiguration.Nodes.
        builder.AddMeshNodes(
            new MeshNode("_Policy", DocumentationNodeProvider.RootNamespace)
            {
                NodeType = "PartitionAccessPolicy",
                Name = "Documentation Access Policy",
                Content = new PartitionAccessPolicy
                {
                    Create = false,
                    Update = false,
                    Delete = false,
                    Comment = true,
                    Thread = true
                }
            },
            new MeshNode($"{WellKnownUsers.Public}_Access",
                $"{DocumentationNodeProvider.RootNamespace}/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{WellKnownUsers.Public} Access",
                MainNode = DocumentationNodeProvider.RootNamespace,
                Content = new AccessAssignment
                {
                    AccessObject = WellKnownUsers.Public,
                    DisplayName = "All authenticated users",
                    Roles = [new RoleAssignment { Role = "Viewer" }]
                }
            });

        return builder;
    }
}

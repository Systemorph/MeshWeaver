using MeshWeaver.ContentCollections;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;

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
                Versioned = false
            }
        });

        builder.ConfigureHub(config => config
            .AddEmbeddedResourceContentCollection(
                "DocContent",
                typeof(DocumentationExtensions).Assembly,
                "Content"));

        return builder;
    }
}

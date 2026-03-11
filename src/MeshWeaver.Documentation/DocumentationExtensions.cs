using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Documentation;

public static class DocumentationExtensions
{
    /// <summary>
    /// Registers MeshWeaver platform documentation as static nodes
    /// and serves documentation content (icons, images) as embedded resources.
    /// </summary>
    public static TBuilder AddDocumentation<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStaticNodeProvider, DocumentationNodeProvider>();
            return services;
        });

        // Register Documentation as a Partition node so it appears in Global Settings
        builder.AddMeshNodes(new MeshNode("Documentation", "Admin/Partition")
        {
            NodeType = "Partition",
            Name = "MeshWeaver Documentation",
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                BasePaths = new HashSet<string> { "Doc" },
                StorageType = "Static",
                Description = "Built-in MeshWeaver platform documentation"
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

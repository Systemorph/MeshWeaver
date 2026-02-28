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

        builder.ConfigureHub(config => config
            .AddEmbeddedResourceContentCollection(
                "DocContent",
                typeof(DocumentationExtensions).Assembly,
                "Content"));

        return builder;
    }
}

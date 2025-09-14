using System.Reflection;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Northwind.Application;
using Microsoft.Extensions.DependencyInjection;

[assembly: NorthwindApplication]

namespace MeshWeaver.Northwind.Application;


/// <summary>
/// This is the configuration of the Northwind application mesh node.
/// </summary>
public class NorthwindApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes
        => [Northwind];

    /// <summary>
    /// Main definition of the mesh node.
    /// </summary>
    public MeshNode Northwind => CreateFromHubConfiguration(
        new ApplicationAddress(nameof(Northwind)),
        nameof(Northwind),
        NorthwindApplicationExtensions.ConfigureNorthwindApplication
    ).WithGlobalServiceRegistry(services =>
        services.AddSingleton<IContentCollectionProvider>(sp =>
        new ContentCollectionProvider(
            new EmbeddedResourceContentCollection(
                "Northwind",
                Assembly.GetExecutingAssembly(),
                typeof(NorthwindApplicationAttribute).Namespace + ".Markdown",
                sp.GetRequiredService<IMessageHub>()
            )
        )));


}

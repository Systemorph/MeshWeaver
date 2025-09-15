using System.Reflection;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using Microsoft.Extensions.DependencyInjection;

[assembly: TodoApplication]

namespace MeshWeaver.Todo;

/// <summary>
/// Mesh node attribute for the Todo application
/// </summary>
public class TodoApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            Address,
            nameof(Todo),
            TodoApplicationExtensions.ConfigureTodoApplication
        ).WithGlobalServiceRegistry(services =>
            services.AddSingleton<IContentCollectionProvider>(sp =>
                new ContentCollectionProvider(
                    new EmbeddedResourceContentCollection(
                        "Todo",
                        Assembly.GetExecutingAssembly(),
                        typeof(TodoApplicationAttribute).Namespace + ".Content",
                        sp.GetRequiredService<IMessageHub>()
                    )
                )))
    ];

    /// <summary>
    /// Address of the Todo application
    /// </summary>
    public static readonly ApplicationAddress Address = new("Todo");
}

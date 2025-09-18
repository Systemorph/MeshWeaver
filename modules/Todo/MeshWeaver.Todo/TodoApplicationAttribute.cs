using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Todo;

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
        ).WithEmbeddedResourceContentCollection("Todo", typeof(TodoApplicationAttribute).Assembly, "Content")
    ];

    /// <summary>
    /// Address of the Todo application
    /// </summary>
    public static readonly ApplicationAddress Address = new("Todo");
}

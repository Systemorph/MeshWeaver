using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;

[assembly: TodoApplication]

namespace MeshWeaver.Todo;

/// <summary>
/// Mesh node attribute for the Todo application
/// </summary>
public class TodoApplicationAttribute : MeshNodeProviderAttribute
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
        )
    ];

    /// <summary>
    /// Address of the Todo application
    /// </summary>
    public static readonly Address Address = AddressExtensions.CreateAppAddress("Todo");
}

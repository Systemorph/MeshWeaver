using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Todo.Domain;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Base class for all Todo tests with proper MonolithMeshTestBase configuration
/// </summary>
public abstract class TodoDataTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// Configures the mesh with Todo application using the attribute
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureMesh(mesh => mesh
                .InstallAssemblies(typeof(TodoApplicationAttribute).Assembly.Location)
            );
    }

    /// <summary>
    /// Configures the client (NO Todo application - it's a client, not a server)
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient().WithType<TodoItem>(nameof(TodoItem));
    }
}

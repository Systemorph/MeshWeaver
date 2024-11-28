using MeshWeaver.Application;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks.Hub;
using Xunit.Abstractions;

namespace MeshWeaver.Notebooks.Test;

public class NotebookTest(ITestOutputHelper output) : MeshTestBase(output)
{
    protected override MeshBuilder CreateBuilder()
        => base
            .CreateBuilder()
            .AddMonolithMesh()
            .ConfigureMesh(mesh =>
                mesh
                    .AddNotebooks()
                    .WithMeshNodeFactory((addressType, id) =>
                    addressType == typeof(ApplicationAddress).FullName && id == TestApplicationAttribute.Test
                        ? MeshExtensions.GetMeshNode(addressType, id, GetType().Assembly.Location)
                        : null));


    public async Task HelloWorld()
    {
        var address = new NotebookAddress();
    }
}

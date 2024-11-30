using MeshWeaver.Application;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh.Contract;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Test
{
    public abstract class ConfiguredMeshTestBase(ITestOutputHelper output) : MeshTestBase(output)
    {
        protected override MeshBuilder CreateBuilder()
            => base
                .CreateBuilder()
                .AddMonolithMesh()
                .ConfigureMesh(mesh =>
                    mesh.WithMeshNodeFactory((addressType, id) =>
                        addressType == typeof(ApplicationAddress).FullName && id == TestApplicationAttribute.Test
                            ? MeshExtensions.GetMeshNode(addressType, id, GetType().Assembly.Location)
                            : null));


    }
}

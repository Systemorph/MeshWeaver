using MeshWeaver.Application;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Mesh.Monolith.Test
{
    public abstract class ConfiguredMeshTestBase(ITestOutputHelper output) : TestBase(output)
    {
        protected record MeshAddress;

        protected record ClientAddress;
        protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
            => builder
                .UseMonolithMesh()
                .ConfigureMesh(mesh =>
                    mesh.WithMeshNodeFactory((addressType, id) =>
                        addressType == typeof(ApplicationAddress).FullName && id == TestApplicationAttribute.Test
                            ? MeshExtensions.GetMeshNode(addressType, id, GetType().Assembly.Location)
                            : null));



        protected IMessageHub CreateMesh(IServiceProvider serviceProvider)
            => ConfigureMesh(new(c => c.Invoke(Services), new MeshAddress())).BuildHub(ServiceProvider);


    }
}

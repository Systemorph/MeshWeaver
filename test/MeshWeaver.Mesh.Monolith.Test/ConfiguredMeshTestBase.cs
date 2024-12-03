using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Test;
using MeshWeaver.Messaging;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test
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
                            ? MeshExtensions.GetMeshNode(addressType, id, typeof(TestApplicationAttribute).Assembly.Location)
                            : null));



        protected IMessageHub CreateMesh(IServiceProvider serviceProvider)
            => ConfigureMesh(new(c => c.Invoke(Services), new MeshAddress()))
                .BuildHub(ServiceProvider);


    }
}

using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test
{
    public abstract class ConfiguredMeshTestBase : TestBase
    {
        protected record ClientAddress;
        protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
            => builder
                .UseMonolithMesh()
                ;

        protected ConfiguredMeshTestBase(ITestOutputHelper output) : base(output)
        {
            
            lazyMesh = new(CreateMesh);

        }


        private readonly Lazy<IMessageHub> lazyMesh;
        protected IMessageHub Mesh => lazyMesh.Value;

        protected IMessageHub CreateMesh()
        {
            var serviceCollection = new ServiceCollection();
            foreach (var service in Services)
            {
                serviceCollection.Add(service);
            }
            return ConfigureMesh(
                    new(
                        c => c.Invoke(serviceCollection),
                        new MeshAddress()
                    )
                )
                .BuildHub(serviceCollection.CreateMeshWeaverServiceProvider());
        }
    }
}

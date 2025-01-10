using System;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.Test
{
    public abstract class MonolithMeshTestBase : TestBase
    {
        protected record ClientAddress() : Address("client", "1");
        protected virtual MeshBuilder ConfigureMesh(MeshBuilder builder)
            => builder
                .UseMonolithMesh()
                ;


        protected MonolithMeshTestBase(ITestOutputHelper output) : base(output)
        {
            var builder = ConfigureMesh(
                new(
                    c => c.Invoke(Services),
                    new MeshAddress()
                )
            );
            Services.AddSingleton(builder.BuildHub);
        }


        protected IMessageHub Mesh => ServiceProvider.GetRequiredService<IMessageHub>();


        protected IMessageHub CreateClient(Func<MessageHubConfiguration, MessageHubConfiguration> config = null) =>
            Mesh.ServiceProvider.CreateMessageHub(new ClientAddress(), config ?? ConfigureClient);

        protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
            configuration;

    }
}

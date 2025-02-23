using System;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MeshWeaver.Hosting.Monolith.TestBase;

public abstract class MonolithMeshTestBase : Fixture.TestBase
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
    protected IRoutingService RoutingService => ServiceProvider.GetRequiredService<IRoutingService>();


    protected IMessageHub GetClient(Func<MessageHubConfiguration, MessageHubConfiguration> config = null)
    {
        return Mesh.ServiceProvider.CreateMessageHub(new ClientAddress(), config ?? ConfigureClient);
    }

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        configuration.WithInitialization((h,_) => RoutingService.RegisterStreamAsync(h));

    public override async Task DisposeAsync()
    {
        Mesh.Dispose();
        await Mesh.Disposal;
        await base.DisposeAsync();
    }
}

using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly:InternalsVisibleTo("MeshWeaver.Hosting")]
namespace MeshWeaver.Mesh.Contract;

public record MeshBuilder
{
    public MeshBuilder(IServiceCollection Services, object Address)
    {
        this.Services = Services;
        this.Address = Address;
        Register();
    }

    private List<Func<MessageHubConfiguration, MessageHubConfiguration>> HubConfiguration { get; } = new();
    public MeshBuilder ConfigureHub(
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfiguration)
    {
        HubConfiguration.Add(hubConfiguration);
        return this;
    }

    private List<Func<MeshConfiguration, MeshConfiguration>> MeshConfiguration { get; } = new();
    public IServiceCollection Services { get; init; }
    public object Address { get; init; }


    public MeshBuilder ConfigureMesh(Func<MeshConfiguration, MeshConfiguration> configuration)
    {
        MeshConfiguration.Add(configuration);
        return this;
    }

    private void Register()
    {
        Services.AddSingleton(_ => BuildMeshConfiguration());

        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = MeshConfiguration;

        ConfigureHub(conf => conf.WithRoutes(routes =>
                routes.WithHandler((delivery, ct) =>
                    delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(Address)
                        ? Task.FromResult(delivery)
                        : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions), ct)))
            .Set(meshConfig)
        );

        Services.AddSingleton(BuildHub);
    }

    public virtual IMessageHub BuildHub(IServiceProvider sp)
    {
        return sp.CreateMessageHub(Address, conf => HubConfiguration.Aggregate(conf, (x, y) => y.Invoke(x)));
    }


    private MeshConfiguration BuildMeshConfiguration() => MeshConfiguration.Aggregate(new MeshConfiguration(), (x, y) => y.Invoke(x));
}

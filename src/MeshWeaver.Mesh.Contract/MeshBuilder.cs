using System.Runtime.CompilerServices;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly:InternalsVisibleTo("MeshWeaver.Hosting")]
namespace MeshWeaver.Mesh;

public record MeshBuilder
{
    public MeshBuilder(Action<Func<IServiceCollection,IServiceCollection>> ServiceConfig, object Address)
    {
        this.ServiceConfig = ServiceConfig;
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

    public MeshBuilder ConfigureServices(Func<IServiceCollection, IServiceCollection> configuration)
    {
        ServiceConfig.Invoke(configuration);
        return this;
    }
    private Action<Func<IServiceCollection, IServiceCollection>> ServiceConfig { get; init; }
    public object Address { get; init; }


    public MeshBuilder ConfigureMesh(Func<MeshConfiguration, MeshConfiguration> configuration)
    {
        MeshConfiguration.Add(configuration);
        return this;
    }

    private void Register()
    {
        ConfigureServices(services => services
            .AddSingleton(_ => BuildMeshConfiguration()).AddSingleton(BuildHub));

        IReadOnlyCollection<Func<MeshConfiguration, MeshConfiguration>> meshConfig = MeshConfiguration;

        ConfigureHub(conf => conf.WithRoutes(routes =>
                routes.WithHandler((delivery, ct) =>
                    delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || delivery.Target.Equals(Address)
                        ? Task.FromResult(delivery)
                        : routes.Hub.ServiceProvider.GetRequiredService<IRoutingService>().DeliverMessage(delivery.Package(routes.Hub.JsonSerializerOptions), ct)))
            .Set(meshConfig)
        );

    }

    public virtual IMessageHub BuildHub(IServiceProvider sp)
    {
        return sp.CreateMessageHub(Address, conf => HubConfiguration.Aggregate(conf, (x, y) => y.Invoke(x)));
    }


    private MeshConfiguration BuildMeshConfiguration() => MeshConfiguration.Aggregate(new MeshConfiguration(), (x, y) => y.Invoke(x));
}

using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Infrastructure;

public class PortalApplication(IMessageHub hub, IRoutingService routingService) : IDisposable
{
    public IMessageHub Hub { get; } = hub.GetHostedHub(new PortalAddress(), c => hub.ServiceProvider.GetRequiredService<ILayoutClient>().Configuration.PortalConfiguration.Aggregate(DefaultPortalConfig(c, routingService), (cc, ccc) => ccc.Invoke(cc)));

    public static MessageHubConfiguration DefaultPortalConfig(MessageHubConfiguration config, IRoutingService routingService)
        => config.WithInitialization(async (hub, _) =>
        {
            var meshRegistry = await routingService.RegisterStreamAsync(hub);
            hub.RegisterForDisposal(meshRegistry);
        });


    public void Dispose()
    {
        Hub.Dispose();
    }
}

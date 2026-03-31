using MeshWeaver.ContentCollections;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Manages the portal hub for a Blazor circuit (one per browser tab).
/// The portal hub is a local sub-hub — no MeshNode in the catalog needed.
/// Lifetime: Scoped (created when circuit starts, disposed when tab closes).
/// </summary>
public class PortalApplication : IDisposable
{
    public IMessageHub Hub { get; }

    public PortalApplication(IMessageHub hub, IRoutingService routingService, INavigationService navigationService)
    {
        Hub = hub.GetHostedHub(AddressExtensions.CreatePortalAddress(),
            c =>
                hub.ServiceProvider.GetRequiredService<ILayoutClient>()
                    .Configuration
                    .PortalConfiguration
                    .Aggregate(DefaultPortalConfig(c, routingService, navigationService),
                        (cc, ccc) => ccc.Invoke(cc)))!;
    }

    public static MessageHubConfiguration DefaultPortalConfig(MessageHubConfiguration config,
        IRoutingService routingService, INavigationService navigationService)
        => config.WithInitialization(async (hub, _) =>
            {
                var meshRegistry = await routingService.RegisterStreamAsync(hub);
                hub.RegisterForDisposal(meshRegistry);
            })
            .AddContentCollections()
            .WithHandler<NavigationRequest>((_, delivery) =>
            {
                var msg = delivery.Message;
                navigationService.NavigateTo(new NavigationOptions(msg.Uri)
                {
                    ForceLoad = msg.ForceLoad,
                    Replace = msg.Replace,
                    Target = msg.Target
                });
                return delivery.Processed();
            });

    public void Dispose()
    {
        Hub.Dispose();
    }
}

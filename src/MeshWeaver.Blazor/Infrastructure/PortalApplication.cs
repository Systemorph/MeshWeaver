using MeshWeaver.ContentCollections;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Blazor.Infrastructure;

public class PortalApplication(IMessageHub hub, IRoutingService routingService, INavigationService navigationService) : IDisposable
{
    public IMessageHub Hub { get; } = hub.GetHostedHub(AddressExtensions.CreatePortalAddress(),
        c =>
            hub.ServiceProvider.GetRequiredService<ILayoutClient>()
                .Configuration
                .PortalConfiguration
                .Aggregate(DefaultPortalConfig(c, routingService, navigationService),
                    (cc, ccc) => ccc.Invoke(cc)))!;

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
                navigationService.NavigateTo(delivery.Message.Uri, delivery.Message.ForceLoad, delivery.Message.Replace);
                return delivery.Processed();
            });


    public void Dispose()
    {
        Hub.Dispose();
    }
}

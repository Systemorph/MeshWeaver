using MeshWeaver.ContentCollections;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown.Export.Configuration;
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
    {
        // Every polymorphic UiControl subtype the portal may receive from a remote layout stream
        // has to be visible to this hub's TypeRegistry so PolymorphicTypeInfoResolver can build
        // the JsonDerivedType mapping for UiControl deserialization. Without this the sub-hub's
        // own registry has only the base types and the stream decode throws:
        //   "The JSON payload for polymorphic interface or abstract type 'UiControl' must
        //    specify a type discriminator."
        config.TypeRegistry.AddMarkdownExportTypes();
        return config.WithInitialization(async (hub, _) =>
            {
                var meshRegistry = await routingService.RegisterStreamAsync(hub);
                hub.RegisterForDisposal(meshRegistry);
            })
            // Route kernel addresses to local hosted hubs — never delegate to grains.
            .WithRoutes(routes => routes.RouteAddressToHostedHub(
                AddressExtensions.KernelType,
                c => c.AddKernelSubHubHandlers()))
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
    }

    public void Dispose()
    {
        Hub.Dispose();
    }
}

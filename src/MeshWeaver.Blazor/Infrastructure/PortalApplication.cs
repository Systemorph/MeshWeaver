using MeshWeaver.ContentCollections;
using MeshWeaver.Kernel;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// Manages the portal hub for a Blazor user session — <b>one portal hub per
/// user identity</b>, shared across every <see cref="PortalApplication"/>
/// instance the DI container creates.
///
/// <para>🚨 Why "per user identity" and not "per scope": <see cref="PortalApplication"/>
/// is registered as <c>Scoped</c>. In Blazor Web hybrid (SSR + Interactive
/// Server) mode every HTTP request gets its own scope (UserContextMiddleware,
/// OnboardingMiddleware, ContentPage SSR pass) AND the interactive WebSocket
/// circuit gets its own scope. With a Guid-per-construction portal address
/// (the previous shape), every scope created a brand-new portal hub:
/// 4–6 transient portals per page navigation, plus one long-lived per circuit.
/// Each portal opened its own remote streams (layout areas, MeshNodeReference
/// reducers, …), each stream wired up its own 45-s <see cref="HeartBeatEvent"/>
/// timer, and the responses to messages submitted from the portal (e.g.
/// <c>AppendUserMessageRequest</c> from chat) routed back to whichever
/// scope-specific portal had submitted — by the time the response arrived
/// that portal was disposed. Symptom: chat "Allocating agent…" spinner that
/// never advances; thousands of stale heartbeats in App Insights.</para>
///
/// <para>The fix: derive the portal hub address from the user's stable identity
/// via <see cref="AccessService.Context"/> / <see cref="AccessService.CircuitContext"/>.
/// All <see cref="PortalApplication"/> instances for the same user resolve to
/// the same hub via <c>hub.GetHostedHub(address, …)</c> (idempotent). Disposal
/// of one wrapper instance does NOT dispose the shared hub — the parent mesh
/// hub owns its lifetime.</para>
///
/// <para>Anonymous / pre-onboarding callers fall back to a stable
/// <c>portal/anonymous</c> address so middleware that runs before the user is
/// resolved still has a single shared portal instead of a per-request one.</para>
/// </summary>
public class PortalApplication : IDisposable
{
    public IMessageHub Hub { get; }

    public PortalApplication(IMessageHub hub, IRoutingService routingService, INavigationService navigationService)
    {
        // Resolve a stable identity for the portal hub address. Falls back to
        // "anonymous" when no AccessContext has been set yet (very first
        // middleware call in the request pipeline before UserContextMiddleware
        // has resolved the user).
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId
                     ?? accessService?.CircuitContext?.ObjectId
                     ?? "anonymous";

        Hub = hub.GetHostedHub(AddressExtensions.CreatePortalAddress(userId),
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
        return config.WithInitialization(hub =>
                hub.RegisterForDisposal(routingService.RegisterStream(hub)))
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

    /// <summary>
    /// 🚨 Does NOT dispose the underlying hub. The hub is shared across every
    /// <see cref="PortalApplication"/> instance for the same user identity (one
    /// PortalApplication created per HTTP request scope + one per circuit), so
    /// disposing it on one scope's teardown would leave subsequent scopes with
    /// a dead hub. The parent mesh hub owns the lifetime of its hosted hubs
    /// and tears them down at app shutdown.
    /// </summary>
    public void Dispose()
    {
        // Intentionally empty — see XML doc.
    }
}

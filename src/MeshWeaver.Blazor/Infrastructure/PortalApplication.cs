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
/// Manages the portal hub for a Blazor session — <b>one portal hub per interactive
/// circuit (per browser tab)</b>, shared across every <see cref="PortalApplication"/>
/// instance the DI container creates within that circuit. The portal hub is a local
/// sub-hub — no MeshNode in the catalog needed.
///
/// <para>🚨 Why "per circuit" and NOT "per construction": <see cref="PortalApplication"/>
/// is registered as <c>Scoped</c>. In Blazor Web hybrid (SSR + Interactive Server) mode
/// every HTTP request gets its own scope (UserContextMiddleware, OnboardingMiddleware,
/// ContentPage SSR/prerender pass) AND the interactive WebSocket circuit gets its own,
/// long-lived scope. With a <c>Guid.NewGuid()</c>-per-construction portal address every
/// scope created a brand-new portal hub: 4–6 transient portals per page navigation plus
/// one per circuit. Each portal opened its own remote streams, each stream wired a 45-s
/// <see cref="HeartBeatEvent"/> timer, and responses to messages submitted from a portal
/// (e.g. chat) routed back to whichever scope-specific portal submitted — by the time the
/// response arrived that scope was disposed. Symptom: chat "Allocating agent…" spinner
/// that never advances; a heartbeat storm.</para>
///
/// <para>The fix: key the portal address on the <b>stable per-circuit id</b> from
/// <see cref="ICircuitContextAccessor.CircuitId"/> (captured once from <c>Circuit.Id</c>
/// by the circuit handler on open). All <see cref="PortalApplication"/> instances within
/// one circuit see the same circuit-scoped accessor instance, hence the same id, hence the
/// same hub via <c>hub.GetHostedHub(address, …)</c> (idempotent — returns the existing hub
/// if already registered). Disposal of one wrapper does NOT dispose the shared hub — the
/// parent mesh hub owns its lifetime.</para>
///
/// <para>SSR / prerender / middleware scopes have no circuit, so
/// <see cref="ICircuitContextAccessor.CircuitId"/> is <see langword="null"/>; those fall
/// back to the user-identity address <c>portal/{userId}</c> (or <c>portal/anonymous</c>
/// before the user is resolved) so the short-lived render passes still share one portal
/// instead of minting a per-request hub. These SSR portals never submit chat.</para>
/// </summary>
public class PortalApplication : IDisposable
{
    public IMessageHub Hub { get; }

    public PortalApplication(
        IMessageHub hub,
        IRoutingService routingService,
        INavigationService navigationService,
        ICircuitContextAccessor circuitContextAccessor,
        PortalErrorSink errorSink)
    {
        // Prefer a stable per-circuit id (one portal hub per browser tab). The accessor is
        // resolved from the SAME DI scope that constructs this PortalApplication (the Blazor
        // circuit scope for interactive components; the HTTP request scope for SSR/middleware)
        // — NOT from hub.ServiceProvider, which is the mesh hub's own container and would not
        // carry the circuit-scoped instance. Within one circuit the accessor is a single
        // instance written once on circuit open, so every PortalApplication in that circuit
        // reads the identical id. When there is no circuit (SSR / prerender / middleware
        // scopes) CircuitId is null and we fall back to the user's stable identity, finally
        // to "anonymous" before the user is resolved.
        var circuitId = circuitContextAccessor.CircuitId;
        string portalId;
        if (!string.IsNullOrEmpty(circuitId))
        {
            portalId = circuitId;
        }
        else
        {
            var accessService = hub.ServiceProvider.GetService<AccessService>();
            portalId = accessService?.Context?.ObjectId
                       ?? accessService?.CircuitContext?.ObjectId
                       ?? "anonymous";
        }

        // The per-circuit user. The portal hub posts layout/agent/model SubscribeRequests on
        // its OWN action-block thread, where the mesh-wide AccessService AsyncLocals are wiped
        // and its persistent fallback is cleared per inbound activity — so without an explicit
        // per-hub identity those posts carry a NULL AccessContext and RLS denies (the empty
        // agent registry). Stamp the circuit user via a per-hub PostPipeline step so every post
        // from this hub is attributed to the user regardless of thread. Read from the
        // per-circuit accessor (written by CircuitAccessHandler) — NOT the mesh-wide
        // persistentCircuitContext, which is shared across circuits and cleared per activity.
        var circuitUser = circuitContextAccessor.UserContext;

        Hub = hub.GetHostedHub(AddressExtensions.CreatePortalAddress(portalId),
            c =>
                hub.ServiceProvider.GetRequiredService<ILayoutClient>()
                    .Configuration
                    .PortalConfiguration
                    .Aggregate(DefaultPortalConfig(c, routingService, navigationService, circuitUser, errorSink),
                        (cc, ccc) => ccc.Invoke(cc)))!;
    }

    public static MessageHubConfiguration DefaultPortalConfig(MessageHubConfiguration config,
        IRoutingService routingService, INavigationService navigationService,
        AccessContext? circuitUser = null, PortalErrorSink? errorSink = null)
    {
        // Surface un-awaited failed posts originating from this portal hub to the GUI as a
        // modal (PortalErrorModal subscribes to the sink). Awaited failures already surface
        // per-callsite via the response callback's OnError; this catches the silent ones
        // (stream.Update writes, fire-and-forget posts). See PortalErrorSink.
        if (errorSink is not null)
            config = config.WithPortalErrorReporting(errorSink);
        // 🚨 SOURCE of the never-null AccessContext invariant for the portal hub.
        // This PostPipeline step runs OUTERMOST (added after UserServicePostPipeline, and
        // AddPipeline wraps outer-first) so it stamps the circuit user BEFORE
        // UserServicePostPipeline's stamp/throw logic runs. When a post already carries an
        // AccessContext (ImpersonateAsHub/AsSystem at the callsite, or a response inheriting
        // the request's identity via ResponseFor) we leave it untouched. When it does not, we
        // stamp the circuit user — so the portal hub's layout/agent/model subscribes are
        // attributed to the logged-in user and RLS returns their data instead of denying.
        if (circuitUser is not null)
            config = config.AddPostPipeline(syncPipeline =>
                syncPipeline.AddPipeline((d, next) =>
                    next(d.AccessContext is null ? d.SetAccessContext(circuitUser) : d)));
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

    public void Dispose()
    {
        // Do NOT dispose Hub here. The hub is shared across every PortalApplication
        // wrapper instance that resolves to the same address (one per circuit, plus the
        // transient SSR/middleware wrappers that fall back to the user-identity address).
        // Its lifetime is owned by the parent mesh hub (GetHostedHub). Disposing it from a
        // transient SSR scope would tear down the live circuit's portal mid-session.
    }
}

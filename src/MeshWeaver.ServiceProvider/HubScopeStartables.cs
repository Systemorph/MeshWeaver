#nullable enable
using Autofac;
using Autofac.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.ServiceProvider;

/// <summary>
/// 🚨 Keeps a child lifetime scope (a hub's scope, <see cref="ServiceProviderExtensions.SetupModules"/>)
/// from re-declaring its ancestors' auto-activated components.
///
/// <para><b>The defect.</b> Autofac runs its startable pass on every child scope built with a
/// configuration action, because the scope's property bag falls back to the root's
/// <c>__ContainerBuildOptions</c>. The pass asks the CHILD registry for <c>AutoActivateService</c>
/// and <see cref="IStartable"/>, and a child registry answers a service by wrapping every registration of
/// it in its parent chain in an <c>ExternalComponentRegistration</c> — with its own resolve-pipeline
/// builder and middleware declarations — kept for the life of the scope. Every
/// <c>RegisterInstance</c> provides <c>AutoActivateService</c>, and <c>Populate</c> registers
/// every <c>AddSingleton(instance)</c> that way, so EVERY hub scope wrapped EVERY instance registration
/// of the mesh (and of each ancestor hub, already wrapped): measured 26 of a node hub's 125 wrappers in
/// a test mesh, 26 of a hosted child hub's 48 — a constant that grows with every module a portal
/// installs. All of it to do nothing: each wrapped registration was activated in the scope that owns it
/// and carries <c>__AutoActivated</c>, so the pass skipped every one of them
/// (<c>Doc/Architecture/PortalHeapIsHubs</c> → "Per-hub DI and type metadata").</para>
///
/// <para><b>The fix.</b> The scope's own property says <see cref="ContainerBuildOptions.IgnoreStartableComponents"/>,
/// so Autofac never runs the pass — unless one of the scope's OWN registrations is something the pass
/// would actually start. Then the scope keeps the inherited options and starts exactly as before. A hub
/// scope's registrations all come from an <see cref="IServiceCollection"/> (<c>Populate</c>), and of what
/// that can produce the pass starts only an <see cref="IStartable"/> service: an
/// <c>AddSingleton(instance)</c> is auto-activated, but as a provided instance the scope does not own —
/// its activation returns the instance and nothing else.</para>
///
/// <para>🚨 Decided from the descriptors, BEFORE the scope is built — never from a
/// <c>ComponentRegistryBuilder.Registered</c> handler that closes over the scope's builder. Measured: a
/// first version that decided it in such a handler kept a disposed hub reachable
/// (<c>StreamReleasesItsHubTest.ADisposedStream_ReleasesItsHub_SoTheHubBecomesCollectable</c> went red;
/// the builder's registration callbacks capture the hub's configuration).</para>
/// </summary>
internal static class HubScopeStartables
{
    // Autofac's own key (MetadataKeys.ContainerBuildOptions, internal). If Autofac renamed it the
    // property would be ignored and the pass would run again — HubScopeDeclaresOnlyItsOwnServicesTest
    // measures the wrappers, so that regression is red rather than silent.
    internal const string ContainerBuildOptionsKey = "__ContainerBuildOptions";

    /// <summary>Makes the scope <paramref name="builder"/> builds start only its own startable components.</summary>
    /// <param name="builder">The child scope's builder.</param>
    /// <param name="services">The registrations the scope is populated with.</param>
    public static void StartOnlyOwnComponents(ContainerBuilder builder, IEnumerable<ServiceDescriptor> services)
    {
        // Always set the scope's OWN value. The property bag falls back to the PARENT scope's, so a scope
        // that left it unset inherited a plain parent's IgnoreStartableComponents and never started its
        // own IStartable (root → plain child → startable grandchild). Only the ignore bit is decided
        // here; every other inherited build option is kept.
        var inherited = builder.Properties.TryGetValue(ContainerBuildOptionsKey, out var value)
                        && value is ContainerBuildOptions options
            ? options
            : ContainerBuildOptions.None;
        builder.Properties[ContainerBuildOptionsKey] = services.Any(d => d.ServiceType == typeof(IStartable))
            ? inherited & ~ContainerBuildOptions.IgnoreStartableComponents
            : inherited | ContainerBuildOptions.IgnoreStartableComponents;
    }
}

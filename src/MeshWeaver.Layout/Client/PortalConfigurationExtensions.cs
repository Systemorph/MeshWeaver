using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Layout.Client;

/// <summary>
/// Lets a plugin configure the PORTAL hub from its own hub's configuration — the seam that makes a
/// plugin able to ship Blazor views.
/// </summary>
public static class PortalConfigurationExtensions
{
    /// <summary>
    /// Contributes <paramref name="portalConfiguration"/> to every portal hub built after this call.
    ///
    /// <para>Written from a NodeType's <c>configuration</c> lambda, which configures that node's OWN
    /// hub — the portal hub is a different hub (one per browser circuit), so it cannot be reached by
    /// returning a modified config. This routes the delegate to it instead:</para>
    ///
    /// <code>
    /// config => config.WithPortalConfiguration(
    ///     portal => portal.AddViews(layout => layout.WithView&lt;MyControl, MyView&gt;()))
    /// </code>
    ///
    /// <para>🚨 <b>This registers a side effect; it does not modify the returned config.</b> Unlike
    /// its neighbours it has to, because the portal hub is built elsewhere and later. The
    /// registration is keyed by this hub's address, so calling it again for the same node — which is
    /// what a recompile does — REPLACES the previous delegate rather than stacking another one. See
    /// <see cref="PortalConfigurationRegistry"/> for why appending would pin the old
    /// <c>AssemblyLoadContext</c> and mix two CLR identities of the same view type.</para>
    ///
    /// <para>A contribution applies to portal hubs created AFTER it, so a plugin installed
    /// mid-session takes effect on the viewer's next page load.</para>
    ///
    /// <para>When no <see cref="PortalConfigurationRegistry"/> is reachable the contribution is
    /// dropped and a warning is logged. Dropping rather than throwing is deliberate — a headless
    /// host (the sidecar, a test mesh with no layout client) has no portal to configure and a plugin
    /// must still load there — but it is never SILENT: a dropped contribution otherwise presents as
    /// a view that simply does not render, with nothing anywhere to say why.</para>
    /// </summary>
    /// <param name="config">The plugin's own hub configuration; returned unchanged.</param>
    /// <param name="portalConfiguration">Applied to each portal hub.</param>
    public static MessageHubConfiguration WithPortalConfiguration(
        this MessageHubConfiguration config,
        Func<MessageHubConfiguration, MessageHubConfiguration> portalConfiguration)
    {
        ArgumentNullException.ThrowIfNull(portalConfiguration);

        // The registry lives in the MESH's container, not this hub's — this hub is still being
        // configured, so its own ServiceProvider does not exist yet.
        var parent = config.ParentHub;
        var registry = parent?.ServiceProvider.GetService<PortalConfigurationRegistry>();

        if (registry is null)
        {
            parent?.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(PortalConfigurationExtensions))
                .LogWarning(
                    "Portal configuration from {Address} was DROPPED: this mesh has no "
                    + "PortalConfigurationRegistry, so it renders no portal (headless host) or the "
                    + "layout client was never added. Any view it registers will not appear.",
                    config.Address);
            return config;
        }

        registry.Set(config.Address.ToString(), portalConfiguration);
        return config;
    }

    /// <summary>
    /// Registers the registry. Called by the layout client's configuration so any mesh that renders
    /// a portal has one, and headless hosts do not.
    /// </summary>
    /// <param name="services">The mesh's service collection.</param>
    public static IServiceCollection AddPortalConfigurationRegistry(this IServiceCollection services)
    {
        services.AddSingleton<PortalConfigurationRegistry>();
        return services;
    }
}

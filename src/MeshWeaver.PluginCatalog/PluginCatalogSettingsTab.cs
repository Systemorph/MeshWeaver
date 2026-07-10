using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The platform-admin "Plugin Catalog" Settings tab — the consumer half of the plugin registry.
/// Reads the catalog from the configured registry instance (<see cref="PluginCatalogOptions.RegistryUrl"/>)
/// over HTTP, shows each module's install status against this instance's install registry, and offers
/// Install / Update. It replaces the old browsable <c>Plugins</c> Space: the catalog is a
/// platform-admin feature, not a partition anyone can navigate to.
///
/// <para>Gated exactly like <c>UserNodeType</c>'s Global Administration tab — visible only when the
/// viewer is a global admin (<c>hub.IsGlobalAdmin(userId)</c>) AND on their own settings page — and
/// grouped under "Administration" beside it.</para>
/// </summary>
public static class PluginCatalogSettingsTab
{
    /// <summary>The settings-menu item id for the Plugin Catalog tab.</summary>
    public const string TabId = "PluginCatalog";

    /// <summary>Registers the Plugin Catalog settings tab provider (global admins only).</summary>
    public static MessageHubConfiguration AddPluginCatalogSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();

        // Same home as the Global Administration tab: the admin's own settings page. Post-v10 the
        // per-user partition is at root, so hubPath == userId (strip the legacy "User/" prefix).
        var hubPath = host.Hub.Address.ToString();
        var nodeOwnerId = hubPath.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
            ? hubPath["User/".Length..]
            : hubPath;
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;
        if (string.IsNullOrEmpty(viewerId)
            || !string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Plugin Catalog",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Document(),
            GroupIcon: FluentIcons.Shield(),
            Order: 320,
            Keywords: ["plugins", "catalog", "install", "modules", "packages", "registry", "extensions"]);

        // Canonical platform-admin check (admin on the Admin partition). Reactive — wait for the
        // POSITIVE with a bounded timeout, StartWith(none) so the menu renders immediately and the tab
        // appears once admin is confirmed (mirrors UserNodeType.GetGlobalAdminTab).
        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    private static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var options = host.Hub.ServiceProvider.GetService<PluginCatalogOptions>() ?? new PluginCatalogOptions();

        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);margin-bottom:12px;\">" +
            "Browse and install plugin modules from the platform registry. Installing a module imports " +
            "its content and compiles its node types live on this instance — no rebuild.</p>"));

        if (string.IsNullOrWhiteSpace(options.RegistryUrl))
            return stack.WithView(Controls.Html(
                "<p style=\"font-size:0.85rem;color:var(--neutral-foreground-hint);\">" +
                "No plugin registry is configured. Set <code>PluginCatalog:RegistryUrl</code> (for " +
                "example <code>https://memex.meshweaver.cloud</code>) to enable the catalog.</p>"));

        var source = new RegistryPackageSource(host.Hub, options.RegistryUrl);

        // Reuse the shared catalog rendering: live package list from the registry joined with this
        // instance's install registry into Install / Update / Installed cards.
        stack = stack.WithView((h, _) => CatalogLayoutAreas.RenderFromSource(
            h, source, options.RegistryRef, description: null, sourceLabel: options.RegistryUrl));
        return stack;
    }
}

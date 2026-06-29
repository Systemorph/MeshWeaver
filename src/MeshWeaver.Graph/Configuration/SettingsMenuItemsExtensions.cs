using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for registering settings menu item providers.
/// Follows the same decentralized pattern as <see cref="NodeMenuItemsExtensions"/>.
/// </summary>
public static class SettingsMenuItemsExtensions
{
    /// <summary>
    /// Registers settings menu item providers. Providers are accumulated
    /// in SettingsMenuProviderCollection stored via config.Set().
    /// </summary>
    public static MessageHubConfiguration AddSettingsMenuItems(
        this MessageHubConfiguration config,
        params SettingsMenuItemProvider[] providers)
    {
        var existing = config.Get<SettingsMenuProviderCollection>()
            ?? new SettingsMenuProviderCollection([]);
        var updated = existing.AddRange(providers);
        return config.Set(updated);
    }

    /// <summary>
    /// Registers static settings menu items. Each definition is wrapped
    /// in a trivial provider that always yields it.
    /// </summary>
    public static MessageHubConfiguration AddSettingsMenuItems(
        this MessageHubConfiguration config,
        params SettingsMenuItemDefinition[] items)
    {
        var providers = items.Select(item =>
        {
            var captured = item;
            return new SettingsMenuItemProvider((_, _) =>
                Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>(new[] { captured }));
        }).ToArray();
        return config.AddSettingsMenuItems(providers);
    }

    /// <summary>
    /// Evaluates all registered providers (subscribe-all-upfront via CombineLatest), filters by
    /// permission, and returns items sorted by Order — reactive (<see cref="IObservable{T}"/>),
    /// re-emitting whenever a provider's live check (e.g. global-admin) resolves.
    /// </summary>
    internal static IObservable<IReadOnlyList<SettingsMenuItemDefinition>>
        EvaluateSettingsMenuItems(
            this MessageHubConfiguration config,
            LayoutAreaHost host,
            RenderingContext ctx,
            Permission userPermissions)
    {
        var collection = config.Get<SettingsMenuProviderCollection>();
        if (collection == null || collection.Providers.Count == 0)
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);

        var streams = collection.Providers.Select(provider =>
            // Skip failing providers so one broken tab can't crash all settings.
            provider(host, ctx).Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(
                _ => Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([])));

        return Observable.CombineLatest(streams)
            .Select(lists =>
            {
                var items = new List<SettingsMenuItemDefinition>();
                foreach (var list in lists)
                    foreach (var item in list)
                        if (item.RequiredPermission == Permission.None
                            || userPermissions.HasFlag(item.RequiredPermission))
                            items.Add(item);
                items.Sort((a, b) => a.Order.CompareTo(b.Order));
                return (IReadOnlyList<SettingsMenuItemDefinition>)items;
            });
    }

    /// <summary>
    /// Registers the default settings menu items (Metadata, NodeTypes, Files,
    /// AccessControl, Groups, EffectiveAccess, Appearance).
    /// Guarded to avoid double registration.
    /// </summary>
    public static MessageHubConfiguration AddDefaultSettingsMenuItems(
        this MessageHubConfiguration config)
    {
        if (config.Get<bool>(nameof(AddDefaultSettingsMenuItems)))
            return config;
        config = config.Set(true, nameof(AddDefaultSettingsMenuItems));

        return config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.MetadataTab,
                Label: "Metadata",
                ContentBuilder: SettingsLayoutArea.BuildMetadataTab,
                Icon: FluentIcons.Info(),
                Order: 0,
                Keywords: ["name", "description", "category", "icon", "order", "id",
                    "namespace", "node type", "state", "version", "created", "modified",
                    "timestamps", "identity", "display"]),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.NodeTypesTab,
                Label: "Node Types",
                ContentBuilder: SettingsLayoutArea.BuildNodeTypesTab,
                Group: "Management",
                Icon: FluentIcons.Document(),
                GroupIcon: FluentIcons.Document(),
                Order: 100,
                Keywords: ["node types", "types", "definitions", "schema", "data model",
                    "creatable types"]),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.FilesTab,
                Label: "Files",
                ContentBuilder: SettingsLayoutArea.BuildFilesTab,
                Group: "Management",
                Icon: FluentIcons.Folder(),
                Order: 110,
                Keywords: ["files", "documents", "uploads", "attachments", "content",
                    "collections", "blobs"]),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.AccessControlTab,
                Label: "Access Control",
                ContentBuilder: SettingsLayoutArea.BuildAccessControlTab,
                Group: "Security",
                Icon: FluentIcons.Shield(),
                GroupIcon: FluentIcons.Shield(),
                Order: 200,
                Keywords: ["access", "permissions", "roles", "assignments", "users",
                    "sharing", "security", "grant", "deny"]),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.GroupsTab,
                Label: "Groups",
                ContentBuilder: SettingsLayoutArea.BuildGroupsTab,
                Group: "Security",
                Icon: FluentIcons.People(),
                Order: 210,
                Keywords: ["groups", "members", "membership", "teams", "roles"]),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.EffectiveAccessTab,
                Label: "Effective Access",
                ContentBuilder: SettingsLayoutArea.BuildEffectiveAccessTab,
                Group: "Security",
                Icon: FluentIcons.PersonSearch(),
                Order: 220,
                Keywords: ["effective access", "permissions", "test", "user", "check",
                    "evaluate", "who can", "audit"]),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.AppearanceTab,
                Label: "Appearance",
                ContentBuilder: SettingsLayoutArea.BuildAppearanceTab,
                Icon: FluentIcons.PaintBrush(),
                Order: 900,
                Keywords: ["appearance", "theme", "color", "dark mode", "light mode",
                    "display", "style", "layout"])
        );
    }
}

/// <summary>
/// Internal holder for accumulated settings menu item providers.
/// </summary>
internal record SettingsMenuProviderCollection(
    IReadOnlyList<SettingsMenuItemProvider> Providers)
{
    public SettingsMenuProviderCollection AddRange(
        IEnumerable<SettingsMenuItemProvider> newProviders)
        => new(Providers.Concat(newProviders).ToList());
}

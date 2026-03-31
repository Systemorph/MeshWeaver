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
            return new SettingsMenuItemProvider((_, _) => YieldSingle(captured));
        }).ToArray();
        return config.AddSettingsMenuItems(providers);
    }

    private static async IAsyncEnumerable<SettingsMenuItemDefinition> YieldSingle(
        SettingsMenuItemDefinition item)
    {
        await Task.CompletedTask;
        yield return item;
    }

    /// <summary>
    /// Evaluates all registered providers, filters by permission, and returns
    /// items sorted by Order.
    /// </summary>
    internal static async Task<IReadOnlyList<SettingsMenuItemDefinition>>
        EvaluateSettingsMenuItemsAsync(
            this MessageHubConfiguration config,
            LayoutAreaHost host,
            RenderingContext ctx,
            Permission userPermissions)
    {
        var collection = config.Get<SettingsMenuProviderCollection>();
        if (collection == null)
            return [];

        var items = new List<SettingsMenuItemDefinition>();
        foreach (var provider in collection.Providers)
        {
            try
            {
                await foreach (var item in provider(host, ctx))
                {
                    if (item.RequiredPermission != Permission.None
                        && !userPermissions.HasFlag(item.RequiredPermission))
                        continue;

                    items.Add(item);
                }
            }
            catch
            {
                // Skip failing providers to prevent one broken tab from crashing all settings
            }
        }

        items.Sort((a, b) => a.Order.CompareTo(b.Order));
        return items;
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
                Order: 0),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.NodeTypesTab,
                Label: "Node Types",
                ContentBuilder: SettingsLayoutArea.BuildNodeTypesTab,
                Group: "Management",
                Icon: FluentIcons.Document(),
                GroupIcon: FluentIcons.Document(),
                Order: 100),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.FilesTab,
                Label: "Files",
                ContentBuilder: SettingsLayoutArea.BuildFilesTab,
                Group: "Management",
                Icon: FluentIcons.Folder(),
                Order: 110),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.AccessControlTab,
                Label: "Access Control",
                ContentBuilder: SettingsLayoutArea.BuildAccessControlTab,
                Group: "Security",
                Icon: FluentIcons.Shield(),
                GroupIcon: FluentIcons.Shield(),
                Order: 200),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.GroupsTab,
                Label: "Groups",
                ContentBuilder: SettingsLayoutArea.BuildGroupsTab,
                Group: "Security",
                Icon: FluentIcons.People(),
                Order: 210),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.EffectiveAccessTab,
                Label: "Effective Access",
                ContentBuilder: SettingsLayoutArea.BuildEffectiveAccessTab,
                Group: "Security",
                Icon: FluentIcons.PersonSearch(),
                Order: 220),

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.AppearanceTab,
                Label: "Appearance",
                ContentBuilder: SettingsLayoutArea.BuildAppearanceTab,
                Icon: FluentIcons.PaintBrush(),
                Order: 900)
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

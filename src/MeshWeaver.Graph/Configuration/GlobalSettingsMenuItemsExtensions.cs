using MeshWeaver.Application.Styles;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for registering global settings menu item providers.
/// Follows the same decentralized pattern as <see cref="SettingsMenuItemsExtensions"/>.
/// </summary>
public static class GlobalSettingsMenuItemsExtensions
{
    /// <summary>
    /// Registers global settings menu item providers. Providers are accumulated
    /// in GlobalSettingsMenuProviderCollection stored via config.Set().
    /// </summary>
    public static MessageHubConfiguration AddGlobalSettingsMenuItems(
        this MessageHubConfiguration config,
        params GlobalSettingsMenuItemProvider[] providers)
    {
        var existing = config.Get<GlobalSettingsMenuProviderCollection>()
            ?? new GlobalSettingsMenuProviderCollection([]);
        var updated = existing.AddRange(providers);
        return config.Set(updated);
    }

    /// <summary>
    /// Registers static global settings menu items. Each definition is wrapped
    /// in a trivial provider that always yields it.
    /// </summary>
    public static MessageHubConfiguration AddGlobalSettingsMenuItems(
        this MessageHubConfiguration config,
        params GlobalSettingsMenuItemDefinition[] items)
    {
        var providers = items.Select(item =>
        {
            var captured = item;
            return new GlobalSettingsMenuItemProvider((_, _) => YieldSingle(captured));
        }).ToArray();
        return config.AddGlobalSettingsMenuItems(providers);
    }

    private static async IAsyncEnumerable<GlobalSettingsMenuItemDefinition> YieldSingle(
        GlobalSettingsMenuItemDefinition item)
    {
        await Task.CompletedTask;
        yield return item;
    }

    /// <summary>
    /// Evaluates all registered providers and returns items sorted by Order.
    /// </summary>
    internal static async Task<IReadOnlyList<GlobalSettingsMenuItemDefinition>>
        EvaluateGlobalSettingsMenuItemsAsync(
            this MessageHubConfiguration config,
            LayoutAreaHost host,
            RenderingContext ctx)
    {
        var collection = config.Get<GlobalSettingsMenuProviderCollection>();
        if (collection == null)
            return [];

        var items = new List<GlobalSettingsMenuItemDefinition>();
        foreach (var provider in collection.Providers)
        {
            try
            {
                await foreach (var item in provider(host, ctx))
                {
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
    /// Registers the default global settings menu items (Data Sources).
    /// Guarded to avoid double registration.
    /// </summary>
    public static MessageHubConfiguration AddDefaultGlobalSettingsMenuItems(
        this MessageHubConfiguration config)
    {
        if (config.Get<bool>(nameof(AddDefaultGlobalSettingsMenuItems)))
            return config;
        config = config.Set(true, nameof(AddDefaultGlobalSettingsMenuItems));

        return config.AddGlobalSettingsMenuItems(
            new GlobalSettingsMenuItemDefinition(
                Id: GlobalSettingsLayoutArea.DataSourcesTab,
                Label: "Data Sources",
                ContentBuilder: GlobalSettingsLayoutArea.BuildDataSourcesTab,
                Icon: FluentIcons.Database(),
                Order: 0)
        );
    }
}

/// <summary>
/// Internal holder for accumulated global settings menu item providers.
/// </summary>
internal record GlobalSettingsMenuProviderCollection(
    IReadOnlyList<GlobalSettingsMenuItemProvider> Providers)
{
    public GlobalSettingsMenuProviderCollection AddRange(
        IEnumerable<GlobalSettingsMenuItemProvider> newProviders)
        => new(Providers.Concat(newProviders).ToList());
}

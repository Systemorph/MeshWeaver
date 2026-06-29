using System.Reactive.Linq;
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
            return new GlobalSettingsMenuItemProvider((_, _) =>
                Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>(new[] { captured }));
        }).ToArray();
        return config.AddGlobalSettingsMenuItems(providers);
    }

    /// <summary>
    /// Evaluates all registered providers (subscribe-all-upfront via CombineLatest) and returns
    /// items sorted by Order — reactive (<see cref="IObservable{T}"/>), re-emitting whenever a
    /// provider's live check (e.g. platform-admin) resolves.
    /// </summary>
    internal static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>>
        EvaluateGlobalSettingsMenuItems(
            this MessageHubConfiguration config,
            LayoutAreaHost host,
            RenderingContext ctx)
    {
        var collection = config.Get<GlobalSettingsMenuProviderCollection>();
        if (collection == null || collection.Providers.Count == 0)
            return Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>([]);

        var streams = collection.Providers.Select(provider =>
            // Skip failing providers so one broken tab can't crash all settings.
            provider(host, ctx).Catch<IReadOnlyList<GlobalSettingsMenuItemDefinition>, Exception>(
                _ => Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>([])));

        return Observable.CombineLatest(streams)
            .Select(lists =>
            {
                var items = lists.SelectMany(l => l).ToList();
                items.Sort((a, b) => a.Order.CompareTo(b.Order));
                return (IReadOnlyList<GlobalSettingsMenuItemDefinition>)items;
            });
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

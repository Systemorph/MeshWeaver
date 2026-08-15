using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
        var providerDelegates = collection?.Providers
            ?? (IReadOnlyList<GlobalSettingsMenuItemProvider>)[];

        var streams = providerDelegates.Select(provider =>
            // Skip failing providers so one broken tab can't crash all settings.
            provider(host, ctx).Catch<IReadOnlyList<GlobalSettingsMenuItemDefinition>, Exception>(
                _ => Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>([])))
            .ToList();

        // Data-contributed tabs (UiContribution nodes with Context = Settings, design #1645): the
        // tab's content is the contributed layout area, embedded generically; gating happens HERE
        // in compiled code — an authenticated viewer always, plus the reactive IsGlobalAdmin
        // stream when the contribution declares AdminOnly. Node-level RequiredPermission has no
        // node to bind to on the global settings surface and is deliberately ignored here.
        streams.Add(ContributedSettingsTabs(host)
            .Catch<IReadOnlyList<GlobalSettingsMenuItemDefinition>, Exception>(
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
    /// The DATA-contributed settings tabs: every <see cref="UiContribution"/> in the shared
    /// mesh-scoped catalog declaring <see cref="UiContribution.SettingsContext"/>, gated in
    /// compiled code — an authenticated, non-virtual viewer always (the settings surface fails
    /// closed for anonymous), plus the live <c>IsGlobalAdmin</c> stream for
    /// <see cref="UiContributionGates.AdminOnly"/>. The tab's content is the contributed layout
    /// area embedded generically, so contributions never introduce a render surface (#1645).
    /// </summary>
    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>>
        ContributedSettingsTabs(LayoutAreaHost host)
    {
        var catalog = host.Hub.ServiceProvider.GetService<UiContributionCatalog>();
        if (catalog is null)
            return Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>([]);

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = accessService?.Context ?? accessService?.CircuitContext;
        var isAuthenticated = !string.IsNullOrEmpty(viewer?.ObjectId) && viewer?.IsVirtual != true;
        if (!isAuthenticated)
            return Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>([]);

        return catalog.Contributions
            .CombineLatest(host.Hub.IsGlobalAdmin().StartWith(false),
                UiContributionProjection.ProjectSettingsTabs);
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
            { LabelKey = "settings.dataSources" }
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

using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The extension point modules use to add sections to the owner's profile page
/// (<c>/{user}/EditProfile</c>). Same decentralized shape as
/// <see cref="SettingsMenuItemsExtensions"/>: providers accumulate on the HUB CONFIGURATION (never
/// in a static registry — Doc/Architecture/NoStaticState), the page subscribes every provider
/// once, merges, sorts by <see cref="ProfileSectionDefinition.Order"/> and filters by the viewer's
/// LATEST permissions.
///
/// <para>A module that core does not reference (the Store's subscription section, for one)
/// contributes from its own configuration, onto the User node type only:</para>
/// <code>
/// config.AddNodeHubContribution(UserNodeType.NodeType, userHub => userHub
///     .AddProfileSections(new ProfileSectionDefinition(
///         Id: "subscription",
///         Title: "Subscription",
///         ContentBuilder: (host, userNode) => BuildSubscription(host, userNode),
///         Order: 500) { TitleKey = "store.profileSubscription" }));
/// </code>
/// <para>Full reference: <c>Doc/GUI/ProfilePage</c>.</para>
/// </summary>
public static class ProfileSectionsExtensions
{
    /// <summary>Shared immutable "nothing contributed" value.</summary>
    private static readonly IReadOnlyList<ProfileSectionDefinition> NoSections = [];

    /// <summary>Registers reactive profile-section providers on this (user) hub.</summary>
    /// <param name="config">The user hub configuration.</param>
    /// <param name="providers">The providers to add.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration AddProfileSections(
        this MessageHubConfiguration config,
        params ProfileSectionProvider[] providers)
    {
        var existing = config.Get<ProfileSectionProviderCollection>()
            ?? new ProfileSectionProviderCollection([]);
        return config.Set(existing.AddRange(providers));
    }

    /// <summary>
    /// Registers static profile sections. Each definition is wrapped in a trivial provider that
    /// always yields it.
    /// </summary>
    /// <param name="config">The user hub configuration.</param>
    /// <param name="sections">The sections to add.</param>
    /// <returns>The configuration, for chaining.</returns>
    public static MessageHubConfiguration AddProfileSections(
        this MessageHubConfiguration config,
        params ProfileSectionDefinition[] sections)
        => config.AddProfileSections(sections
            .Select(section => new ProfileSectionProvider((_, _) =>
                Observable.Return<IReadOnlyList<ProfileSectionDefinition>>([section])))
            .ToArray());

    /// <summary>
    /// The live, UNFILTERED contributed-section set: every registered provider subscribed once,
    /// merged and sorted by order. A faulting provider degrades to "no sections" so one broken
    /// module cannot take the profile page down; the permission filter is applied separately
    /// (<see cref="FilterByPermission"/>) against the latest permission value, for the reason
    /// <c>SettingsMenuItemsExtensions.ObserveSettingsMenuItems</c> documents (#1962).
    /// </summary>
    /// <param name="config">The user hub configuration.</param>
    /// <param name="host">The profile area's host.</param>
    /// <param name="ctx">The profile area's rendering context.</param>
    internal static IObservable<IReadOnlyList<ProfileSectionDefinition>> ObserveProfileSections(
        this MessageHubConfiguration config, LayoutAreaHost host, RenderingContext ctx)
    {
        var providers = config.Get<ProfileSectionProviderCollection>()?.Providers ?? [];

        var streams = providers.Select(provider => Observable
                .Defer(() => provider(host, ctx))
                // Seeded so CombineLatest renders the page at once instead of stalling on the
                // slowest provider (the node menu does the same).
                .StartWith(NoSections)
                .Catch<IReadOnlyList<ProfileSectionDefinition>, Exception>(
                    _ => Observable.Return<IReadOnlyList<ProfileSectionDefinition>>([])))
            .ToList();

        // Data-contributed sections (UiContribution nodes with Context = Profile): the lane for a
        // module compiled from mesh content, which cannot reach this hub's configuration. Added
        // unconditionally and read LIVE, so a newly declared section appears without a recycle.
        streams.Add(ContributedProfileSections(host)
            .StartWith(NoSections)
            .Catch<IReadOnlyList<ProfileSectionDefinition>, Exception>(
                _ => Observable.Return<IReadOnlyList<ProfileSectionDefinition>>([])));

        return Observable.CombineLatest(streams).Select(Merge);
    }

    /// <summary>
    /// The DATA-contributed profile sections: every <see cref="UiContribution"/> in the shared,
    /// mesh-scoped <see cref="UiContributionCatalog"/> declaring
    /// <see cref="UiContribution.ProfileContext"/>, projected through the closed gate vocabulary in
    /// compiled code (<see cref="UiContributionProjection.ProjectProfileSections"/>). The twin of
    /// <c>SettingsMenuItemsExtensions.ContributedSettingsTabs</c>: fails closed for an anonymous
    /// or virtual viewer, and re-projects on every catalog change and every change of the user
    /// node's shape.
    /// </summary>
    private static IObservable<IReadOnlyList<ProfileSectionDefinition>> ContributedProfileSections(
        LayoutAreaHost host)
    {
        var catalog = host.Hub.ServiceProvider.GetService<UiContributionCatalog>();
        if (catalog is null)
            return Observable.Return(NoSections);

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = accessService?.Context ?? accessService?.CircuitContext;
        if (string.IsNullOrEmpty(viewer?.ObjectId) || viewer.IsVirtual)
            return Observable.Return(NoSections);

        var viewerId = accessService.ViewerId();
        var userPath = host.Hub.Address.ToString();
        return Observable.Defer(() =>
        {
            var userNode = host.Workspace.GetMeshNodeStream()
                .Catch<MeshNode, Exception>(_ => Observable.Return<MeshNode>(null!));
            return catalog.Contributions
                .CombineLatest(userNode, host.Hub.IsGlobalAdmin().StartWith(false),
                    (contributions, node, isAdmin) => UiContributionProjection
                        .ProjectProfileSections(contributions, userPath, node, isAdmin, viewerId));
        });
    }

    /// <summary>Flattens, de-duplicates by id (first registration wins) and sorts by order.</summary>
    /// <param name="lists">One section list per provider.</param>
    internal static IReadOnlyList<ProfileSectionDefinition> Merge(
        IEnumerable<IReadOnlyList<ProfileSectionDefinition>?> lists)
        => lists.Where(list => list is not null)
            .SelectMany(list => list!)
            .GroupBy(section => section.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(section => section.Order)
            .ToList();

    /// <summary>
    /// The profile page's permission gate — PURE, so both directions are assertable without a hub.
    /// A section declaring <see cref="Permission.None"/> shows to every viewer of the page; any
    /// other requirement must be held on the user node.
    /// </summary>
    /// <param name="sections">The unfiltered, sorted sections.</param>
    /// <param name="permissions">The viewer's effective permissions on the user node.</param>
    internal static IReadOnlyList<ProfileSectionDefinition> FilterByPermission(
        IReadOnlyList<ProfileSectionDefinition> sections, Permission permissions)
        => sections
            .Where(s => s.RequiredPermission == Permission.None
                        || permissions.HasFlag(s.RequiredPermission))
            .ToList();
}

/// <summary>Holder for the accumulated profile-section providers carried on a hub configuration.</summary>
/// <param name="Providers">The registered providers, in registration order.</param>
internal record ProfileSectionProviderCollection(IReadOnlyList<ProfileSectionProvider> Providers)
{
    /// <summary>A new collection with <paramref name="more"/> appended.</summary>
    /// <param name="more">The providers to append.</param>
    public ProfileSectionProviderCollection AddRange(IEnumerable<ProfileSectionProvider> more)
        => new(Providers.Concat(more).ToList());
}

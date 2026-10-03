using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The <b>person app</b> — the settings page of the signed-in person's OWN partition root,
/// <c>/{user}/Settings</c>, titled with the person's name. It holds everything that acts on the
/// PERSON: their profile, their account, their preferences (language, time zone, theme — in one
/// place), their notifications, API tokens, connected instances, subscription and who they share
/// their partition with.
///
/// <para><b>The ownership rule</b> (see <c>Doc/Architecture/AdminApp</c>): every settings tab lives in
/// the app of the thing it CHANGES — the instance (<see cref="AdminAppNodeType"/>), the person (this
/// app) or a node (its ⋯ → Settings…). A tab that acts on the signed-in person therefore never
/// appears on a node's or the instance's settings page. A module registers a personal tab through
/// <see cref="AddPersonAppTab"/>: the provider may ride every node hub (as the personal tabs always
/// did), but it yields the tab ONLY on the viewer's own user root, and an old link to it on any other
/// settings page redirects here.</para>
/// </summary>
public static class PersonApp
{
    /// <summary>The Profile tab — the owner's profile editor (<c>/{user}/EditProfile</c>), embedded.</summary>
    public const string ProfileTab = "Profile";

    /// <summary>The Account tab — who the session is, and what it may do.</summary>
    public const string AccountTab = WhoAmISettingsTab.AccountTabId;

    /// <summary>The Preferences tab — language, time zone and theme.</summary>
    public const string PreferencesTab = "Preferences";

    /// <summary>The Sharing tab — Access control on the person's own partition root.</summary>
    public const string SharingTab = "Sharing";

    /// <summary>The retired Appearance tab id; its content is part of <see cref="PreferencesTab"/>.</summary>
    internal const string AppearanceTabId = "Appearance";

    /// <summary>Slot of the Profile tab (the app's first tab — <c>/{user}/Settings</c> lands on it).</summary>
    public const int ProfileOrder = 0;
    /// <summary>Slot of the Account tab.</summary>
    public const int AccountOrder = 10;
    /// <summary>Slot of the Preferences tab.</summary>
    public const int PreferencesOrder = 20;
    /// <summary>Slot of the Notifications tab.</summary>
    public const int NotificationsOrder = 30;
    /// <summary>Slot of the API tokens tab.</summary>
    public const int ApiTokensOrder = 40;
    /// <summary>Slot of the Connected instances tab.</summary>
    public const int ConnectedInstancesOrder = 50;
    /// <summary>Slot of the Subscription tab.</summary>
    public const int SubscriptionOrder = 60;
    /// <summary>Slot of the Sharing tab.</summary>
    public const int SharingOrder = 70;

    /// <summary>The person app's OWN tab ids (and the retired Appearance id, which aliases into
    /// Preferences). A data-contributed person-app tab can never take one: the settings fold drops a
    /// contributed tab whose id is already on the page, and seed validation reports it.</summary>
    public static readonly ImmutableHashSet<string> BuiltInTabIds = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase, ProfileTab, AccountTab, PreferencesTab, SharingTab, AppearanceTabId);

    /// <summary>The node-settings tabs that mean nothing in the person app: the partition's metadata,
    /// node types, groups and effective-access probe are node management, not the person's things;
    /// its Access control is re-offered as <see cref="SharingTab"/>.</summary>
    internal static readonly ImmutableArray<string> HiddenDefaultTabs =
    [
        SettingsLayoutArea.MetadataTab, SettingsLayoutArea.NodeTypesTab, SettingsLayoutArea.GroupsTab,
        SettingsLayoutArea.EffectiveAccessTab, SettingsLayoutArea.AccessControlTab,
        SettingsLayoutArea.VersionsTab,
    ];

    /// <summary>The person app of <paramref name="userId"/>: <c>/{userId}/Settings</c>.</summary>
    /// <param name="userId">The person's id (their partition key).</param>
    public static string Href(string userId) =>
        "/" + new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea).ToHref(userId);

    /// <summary>One tab of the person app: <c>/{userId}/Settings/{tabId}</c>. Built through
    /// <see cref="LayoutAreaReference.ToHref(object)"/> so it cannot drift from the menu's own links.</summary>
    /// <param name="userId">The person's id.</param>
    /// <param name="tabId">The tab id.</param>
    public static string TabHref(string userId, string tabId) =>
        "/" + new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = tabId }.ToHref(userId);

    /// <summary>
    /// True when <paramref name="host"/> renders on the VIEWER's own user root — the only hub the
    /// person app's tabs appear on. The user root's path IS the person's id (post-v10), so the test
    /// is "a single-segment hub path equal to the viewer's id"; nobody else's root and no node below
    /// it qualifies.
    /// </summary>
    /// <param name="host">The layout host.</param>
    public static bool IsPersonAppHub(this LayoutAreaHost host)
        => IsOwnRoot(host.Hub.Address.ToString(),
            host.Hub.ServiceProvider.GetService<AccessService>().ViewerId());

    /// <summary>Pure form of <see cref="IsPersonAppHub"/>.</summary>
    internal static bool IsOwnRoot(string hubPath, string? viewerId)
        => !string.IsNullOrEmpty(viewerId)
           && SettingsMenuItemsExtensions.IsPartitionRoot(hubPath)
           && string.Equals(hubPath, viewerId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="tab"/> as a tab of the person app. The provider may be registered on
    /// every node hub; it yields the tab ONLY on the viewer's own user root
    /// (<see cref="IsPersonAppHub"/>), and nothing anywhere else — so a personal tab never shows on a
    /// Space's, a node's or the instance's settings page.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="tab">The tab. Its <see cref="SettingsMenuItemDefinition.RequiredPermission"/> still
    /// applies, against the viewer's permissions on their own root.</param>
    /// <param name="relocated"><c>true</c> (the default) when an old link to the tab on another
    /// settings page must redirect into the person app.</param>
    public static MessageHubConfiguration AddPersonAppTab(
        this MessageHubConfiguration config, SettingsMenuItemDefinition tab, bool relocated = true)
    {
        var withProvider = config.AddSettingsMenuItems(new SettingsMenuItemProvider((host, _) => PersonOnlyTab(host, tab)));
        return relocated ? withProvider.RelocateSettingsTabsToPersonApp(tab.Id) : withProvider;
    }

    /// <summary>
    /// Records <paramref name="tabIds"/> as tabs of the person app: a settings page on any hub but the
    /// viewer's own root that is asked for one of these ids redirects to
    /// <see cref="TabHref"/> for the viewer.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="tabIds">The tab ids.</param>
    public static MessageHubConfiguration RelocateSettingsTabsToPersonApp(
        this MessageHubConfiguration config, params string[] tabIds)
        => tabIds.Aggregate(config, (c, id) => c.RelocateSettingsTabToPersonApp(id, id));

    /// <summary>Records that <paramref name="fromTabId"/> now lives in the person app as
    /// <paramref name="toTabId"/>.</summary>
    internal static MessageHubConfiguration RelocateSettingsTabToPersonApp(
        this MessageHubConfiguration config, string fromTabId, string toTabId)
    {
        var existing = config.Get<PersonAppRelocatedTabs>() ?? PersonAppRelocatedTabs.Empty;
        return config.Set(existing with { Map = existing.Map.SetItem(fromTabId, toTabId) });
    }

    /// <summary>
    /// The person gate for one tab: the tab on the viewer's own user root, nothing anywhere else.
    /// Public so a module whose provider is itself public API can delegate to it; everything else
    /// registers through <see cref="AddPersonAppTab"/>.
    /// </summary>
    /// <param name="host">The layout host of the settings page being rendered.</param>
    /// <param name="tab">The tab to contribute.</param>
    public static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> PersonOnlyTab(
        LayoutAreaHost host, SettingsMenuItemDefinition tab)
        => Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>(host.IsPersonAppHub() ? [tab] : []);

    /// <summary>
    /// The person app's own tabs and page shape, applied by the User node type: Profile, Account,
    /// Preferences and Sharing; the node-management defaults hidden.
    /// </summary>
    /// <param name="config">The User hub's configuration.</param>
    internal static MessageHubConfiguration AddPersonAppTabs(this MessageHubConfiguration config)
        => config
            .HideSettingsTabs([.. HiddenDefaultTabs])
            .AddPersonAppTab(ProfileTabDefinition, relocated: false)
            .AddPersonAppTab(WhoAmISettingsTab.AccountTab, relocated: false)
            .AddPersonAppTab(PreferencesTabDefinition, relocated: false)
            .AddPersonAppTab(SharingTabDefinition, relocated: false)
            // The theme moved into Preferences; an old link to the Appearance tab lands there.
            .AliasSettingsTab(AppearanceTabId, PreferencesTab)
            // The partition's Access control is this app's Sharing tab.
            .AliasSettingsTab(SettingsLayoutArea.AccessControlTab, SharingTab);

    private static SettingsMenuItemDefinition ProfileTabDefinition { get; } = new(
        Id: ProfileTab,
        Label: "Profile",
        ContentBuilder: (host, stack, _) => stack.WithView(
            // REUSED, not copied: the profile editor IS the EditProfile area.
            Controls.LayoutArea(host.Hub.Address, UserActivityLayoutAreas.EditProfileArea)
                .WithShowProgress(false),
            "ProfileContent"),
        Icon: FluentIcons.Person(),
        Order: ProfileOrder,
        RequiredPermission: Permission.Update,
        Keywords: ["profile", "picture", "photo", "avatar", "name", "display name", "email", "bio",
            "links", "showcase"])
    { LabelKey = "settings.profile" };

    private static SettingsMenuItemDefinition PreferencesTabDefinition { get; } = new(
        Id: PreferencesTab,
        Label: "Preferences",
        ContentBuilder: BuildPreferencesTab,
        Icon: FluentIcons.Settings(),
        Order: PreferencesOrder,
        RequiredPermission: Permission.Update,
        Keywords: ["preferences", "time zone", "timezone", "clock", "display", "locale",
            "region", "utc", "dst", "language", "sprache", "deutsch", "german", "english",
            "translation", "appearance", "theme", "color", "dark mode", "light mode", "style"])
    { LabelKey = "settings.preferences" };

    private static SettingsMenuItemDefinition SharingTabDefinition { get; } = new(
        Id: SharingTab,
        Label: "Sharing",
        ContentBuilder: (host, stack, node) => SettingsLayoutArea.BuildAccessControlTab(host,
            stack
                .WithView(Controls.H2(host.Localize("settings.sharing")).WithStyle("margin: 0 0 8px 0;"))
                .WithView(Controls.Markdown(host.Localize("personApp.sharingIntro"))),
            node),
        Icon: FluentIcons.Share(),
        Order: SharingOrder,
        RequiredPermission: Permission.Update,
        Keywords: ["sharing", "share", "access", "permissions", "roles", "grant", "who can see"])
    { LabelKey = "settings.sharing" };

    /// <summary>
    /// Preferences — the manual override for the per-viewer display preferences (time zone and UI
    /// language, bound DIRECTLY to the user node via <see cref="MeshNodeContentEditorControl"/>;
    /// setting either writes a non-empty value, which the browser auto-detect then never overwrites —
    /// see <see cref="TimeZonePreference"/> and <see cref="LocalePreference"/>) and the theme. ONE
    /// place: the profile page no longer carries language and time zone.
    /// </summary>
    private static UiControl BuildPreferencesTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var access = host.Hub.ServiceProvider.GetService<AccessService>();

        stack = stack.WithView(Controls.H2(access.Localize("settings.preferences"))
            .WithStyle("margin: 0 0 8px 0;"));
        if (node is null)
            return stack.WithView(Controls.Markdown(access.Localize("ui.mdProfileNotFound")));

        foreach (var key in UserNodeType.PreferencesDescriptionKeys)
            stack = stack.WithView(Controls.Markdown(access.Localize(key))
                .WithStyle("color: var(--neutral-foreground-hint); margin-bottom: 8px;"));

        stack = stack.WithView(new MeshNodeContentEditorControl(node.Path)
        {
            CanEdit = true,
            Fields = UserNodeType.PreferenceFields(key => access.Localize(key))
        });

        return stack
            .WithView(Controls.H3(access.Localize("settings.appearance")).WithStyle("margin: 24px 0 8px 0;"))
            .WithView(new AppearanceControl());
    }
}

/// <summary>
/// Tab ids that moved into the person app, mapped to the person-app tab that carries them
/// (<see cref="PersonApp.RelocateSettingsTabsToPersonApp"/>). Held on the hub configuration — per
/// mesh, never static.
/// </summary>
/// <param name="Map">Old tab id → person-app tab id.</param>
internal sealed record PersonAppRelocatedTabs(ImmutableDictionary<string, string> Map)
{
    /// <summary>No relocated tabs.</summary>
    public static PersonAppRelocatedTabs Empty { get; } =
        new(ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase));
}

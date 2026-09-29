using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Per-user "Notifications" settings tab — lets every user choose, PER FEATURE (approvals, inbox,
/// triage, access grants, chat, system, and any feature a module registers), which channels reach
/// them: the in-app bell, Microsoft Teams and email. One section per feature, each the STANDARD
/// node-content editor bound directly to that feature's
/// <c>{userId}/_Settings/Notifications/{feature}</c> <see cref="NotificationFeaturePreference"/> node
/// (each checkbox writes one field through the node stream — no /data replica, no save
/// subscription). The node is created on first view with the preference the user already had
/// (<see cref="NotificationFeaturePreferenceNodeType.EnsureExists"/>), so opening the tab changes no
/// delivery. Visible to everyone (<see cref="Permission.None"/>).
///
/// <para>These preferences drive <see cref="NotificationService.Raise"/>. The default for a feature
/// the user never set is the bell and Teams. The AI-triage routing
/// (<c>NotificationRule</c>/<c>NotificationChannel</c>) still layers on top for users who author
/// rules; the deterministic email path defers to it for them.</para>
/// </summary>
public static class NotificationsSettingsTab
{
    public const string TabId = "Notifications";

    public static MessageHubConfiguration AddNotificationsSettingsTab(this MessageHubConfiguration config)
        // A tab of the PERSON APP (PersonApp.AddPersonAppTab): the preferences are the signed-in
        // person's, so the tab shows on their own settings page only.
        => config.AddPersonAppTab(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Notifications",
                ContentBuilder: BuildContent,
                Icon: FluentIcons.Alert(),
                Order: PersonApp.NotificationsOrder,
                RequiredPermission: Permission.None,
                Keywords: ["notifications", "teams", "email", "bell", "channels", "approvals", "inbox", "triage", "apps", "deliver quietly"])
            { LabelKey = "settings.notifications" });

    /// <summary>
    /// The features the tab offers a row for: the platform's own
    /// (<see cref="NotificationFeatures.BuiltIn"/>) plus every <see cref="NotificationFeatureDescriptor"/>
    /// a module registered, one row per feature key, in <see cref="NotificationFeatureDescriptor.Order"/>.
    /// </summary>
    internal static IReadOnlyList<NotificationFeatureDescriptor> Features(IServiceProvider services)
        => NotificationFeatures.BuiltIn
            .Concat(services.GetServices<NotificationFeatureDescriptor>())
            // A key that could not name a preference node (NotificationFeatures.IsValidKey) gets
            // no row: Raise refuses such a feature, so there is nothing to choose channels for.
            .Where(d => NotificationFeatures.IsValidKey(d.Feature))
            .GroupBy(d => d.Feature, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(d => d.Order)
            .ThenBy(d => d.Feature, StringComparer.Ordinal)
            .ToList();

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;

        stack = stack.WithView(Controls.H2(host.Localize("settings.notifications")).WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(host.Localize("settings.notificationsIntro")));
        stack = stack.WithView(Controls.Markdown(host.Localize("settings.notificationsTeamsHint")));

        if (string.IsNullOrEmpty(userId))
        {
            stack = stack.WithView(Controls.Markdown(host.Localize("ui.mdSignInForNotifs")));
            return stack;
        }

        // One section per feature: ensure the feature's node exists (create-on-absent, seeded with
        // what the user already had), then bind the standard node-content editor to it — each bool
        // renders as a labelled checkbox that auto-saves through the node stream. No hand-rolled
        // form, no /data copy.
        foreach (var feature in Features(host.Hub.ServiceProvider))
        {
            var key = feature.Feature;
            stack = stack.WithView(Controls.H3(host.Localize(feature.LabelKey)).WithStyle("margin: 16px 0 4px 0;"));
            stack = stack.WithView((h, _) => NotificationFeaturePreferenceNodeType
                .EnsureExists(h.Hub, userId!, key)
                .Select(path => (UiControl?)MeshNodeContentEditorControl.ForType(path, typeof(NotificationFeaturePreference)))
                // A seed refused because the legacy settings could not be read is shown, not
                // swallowed — and nothing was written, so the person's choice is intact.
                .Catch((Exception _) => Observable.Return<UiControl?>(
                    Controls.Markdown(host.Localize("ui.mdNotifPrefsUnavailable"))))
                .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingNotifPrefs"))));
        }

        // iOS Settings → Notifications: below the kinds, one section per INSTALLED APP — its
        // master switch, "Deliver quietly", and which channels it may use. Listed from the same
        // single-partition query the home's Apps grid makes; each editor binds the app's
        // {userId}/_Settings/Notifications/Apps/{appId} node, created on first view with the default
        // the dispatcher already applies, so opening the tab changes no delivery.
        stack = stack.WithView(Controls.H2(host.Localize("settings.notificationsApps")).WithStyle("margin: 24px 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(host.Localize("settings.notificationsAppsIntro")));
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace()
            .GetQuery($"{NotificationAppPreferenceNodeType.NodeType}|tab|{userId}",
                $"path:{userId}/{AppNodeType.UserNamespace} scope:children nodeType:{AppNodeType.NodeType} sort:name")
            .Take(1)
            .Select(apps => (UiControl?)AppSections(host, userId!, apps ?? []))
            .Catch((Exception _) => Observable.Return<UiControl?>(
                Controls.Markdown(host.Localize("ui.mdNotifPrefsUnavailable"))))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingNotifPrefs"))));

        return stack;
    }

    private static UiControl AppSections(LayoutAreaHost host, string userId, IEnumerable<MeshNode> apps)
    {
        var sections = Controls.Stack;
        var any = false;
        foreach (var app in apps.Where(a => NotificationApps.IsValidKey(a.Id)))
        {
            any = true;
            var id = app.Id;
            sections = sections.WithView(Controls.H3(string.IsNullOrWhiteSpace(app.Name) ? id : app.Name!)
                .WithStyle("margin: 16px 0 4px 0;"));
            sections = sections.WithView((h, _) => NotificationAppPreferenceNodeType
                .EnsureExists(h.Hub, userId, id)
                .Select(path => (UiControl?)MeshNodeContentEditorControl.ForType(path, typeof(NotificationAppPreference)))
                .Catch((Exception _) => Observable.Return<UiControl?>(
                    Controls.Markdown(host.Localize("ui.mdNotifPrefsUnavailable"))))
                .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoadingNotifPrefs"))));
        }
        return any ? sections : Controls.Markdown(host.Localize("settings.notificationsAppsNone"));
    }
}

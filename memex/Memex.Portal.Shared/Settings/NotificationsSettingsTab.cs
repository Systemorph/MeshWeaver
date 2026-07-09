using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
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
/// Per-user "Notifications" settings tab — lets every user choose, per notification category
/// (approvals, access grants, chat, system), whether it reaches them via the in-app bell and/or by
/// email. Data-binds the STANDARD node-content editor directly to the user's
/// <c>{userId}/_Settings/Notifications</c> <see cref="NotificationSettings"/> node (each checkbox
/// writes one field through the node stream — no /data replica, no save subscription). Visible to
/// everyone (<see cref="Permission.None"/>).
///
/// <para>These deterministic preferences drive <see cref="NotificationService.Dispatch"/>. The
/// advanced AI-triage routing (<c>NotificationRule</c>/<c>NotificationChannel</c>) still layers on top
/// for users who author routing rules; the deterministic email path defers to it for them.</para>
/// </summary>
public static class NotificationsSettingsTab
{
    public const string TabId = "Notifications";

    public static MessageHubConfiguration AddNotificationsSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Notifications",
                ContentBuilder: BuildContent,
                Group: "Preferences",
                Icon: FluentIcons.Alert(),
                GroupIcon: FluentIcons.Person(),
                Order: 240,
                RequiredPermission: Permission.None));

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? accessService?.CircuitContext?.ObjectId;

        stack = stack.WithView(Controls.H2("Notifications").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "Choose where each kind of notification reaches you — the in-app **bell** and/or **email**. " +
            "The bell is on by default for everything; email is on by default for access grants and approvals."));

        if (string.IsNullOrEmpty(userId))
        {
            stack = stack.WithView(Controls.Markdown("_Sign in to manage your notification preferences._"));
            return stack;
        }

        // Ensure the settings node exists (create-on-absent with defaults), then bind the standard
        // node-content editor to it — each bool renders as a labelled checkbox that auto-saves through
        // the node stream. No hand-rolled form, no /data copy.
        stack = stack.WithView((h, _) => NotificationSettingsNodeType
            .EnsureExists(h.Hub, userId!)
            .Select(path => (UiControl?)MeshNodeContentEditorControl.ForType(path, typeof(NotificationSettings)))
            .StartWith((UiControl?)Controls.Markdown("_Loading notification preferences…_")));

        return stack;
    }
}

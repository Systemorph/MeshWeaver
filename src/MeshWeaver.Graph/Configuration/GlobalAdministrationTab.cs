using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The Admin app's "Administrators" tab: who the platform administrators are — the
/// <c>AccessAssignment</c> nodes in <c>Admin/_Access</c> — and adding or removing one.
///
/// <para><b>ONE tab.</b> The app used to carry this list twice: "Global Administration" (a
/// <c>MeshSearch</c> over <c>Admin/_Access</c> plus "+ Add Admin") and the Admin node's default
/// "Access Control" tab, which listed the SAME grants with add and remove. Both are this tab now:
/// it embeds the Admin node's Access Control area, so a grant added here lands at
/// <c>Admin/_Access/{subject}_Access</c> — the platform-admin shape — and an old
/// <c>/Admin/Settings/AccessControl</c> link redirects here.</para>
///
/// <para>It used to be a tab of the admin's OWN user settings page (<c>/{user}/Settings/GlobalAdmin</c>),
/// which mixed platform administration into a person's settings; that link redirects here too.</para>
///
/// <para>The area runs as the viewer and shows only what the viewer may read — the tab is registered
/// through <see cref="AdminAppNodeType.AddAdminAppTab"/>, so only a confirmed platform admin sees it
/// at all, and the grants stay exactly as readable as they were.</para>
/// </summary>
public static class GlobalAdministrationTab
{
    /// <summary>The tab id — unchanged from the user-settings tab it replaces, so old links redirect.</summary>
    public const string TabId = "GlobalAdmin";

    /// <summary>The tab definition.</summary>
    internal static SettingsMenuItemDefinition Definition { get; } = new(
        Id: TabId,
        Label: "Administrators",
        ContentBuilder: (host, stack, node) => Build(host, stack, node),
        Group: AdminAppNodeType.PeopleGroup,
        Icon: FluentIcons.Shield(),
        GroupIcon: FluentIcons.People(),
        Order: AdminAppNodeType.PeopleOrder,
        Keywords: ["administrators", "global admin", "platform admin", "administration", "access control",
            "users", "system", "access", "grants", "roles"])
    {
        LabelKey = "settings.administrators",
        GroupKey = AdminAppNodeType.PeopleGroupKey,
    };

    private static UiControl Build(LayoutAreaHost host, StackControl stack, MeshNode? node)
        => SettingsLayoutArea.BuildAccessControlTab(host,
            stack
                .WithView(Controls.H2(host.Localize("settings.administrators")).WithStyle("margin: 0 0 8px 0;"))
                .WithView(Controls.Markdown(host.Localize("globalAdmin.intro"))),
            node);
}

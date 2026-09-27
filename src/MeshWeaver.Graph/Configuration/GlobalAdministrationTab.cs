using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The Admin app's "Global Administration" tab: who the platform administrators are (the
/// <c>AccessAssignment</c> nodes in <c>Admin/_Access</c>) and the "+ Add Admin" flow, which reuses
/// the Access Control area's subject/role picker scoped to the <c>Admin</c> partition so a new grant
/// lands at <c>Admin/_Access/{subject}_Access</c> — the platform-admin shape.
///
/// <para>It used to be a tab of the admin's OWN user settings page (<c>/{user}/Settings/GlobalAdmin</c>),
/// which mixed platform administration into a person's settings; that link now redirects here. The
/// data sources it also listed have their own Admin-app tab (Data Sources).</para>
///
/// <para>The list is a <c>MeshSearch</c> over <c>Admin/_Access</c>, so it runs as the viewer and shows
/// only what the viewer may read — the tab is registered through
/// <see cref="AdminAppNodeType.AddAdminAppTab"/>, so only a confirmed platform admin sees it at all,
/// and the grants stay exactly as readable as they were.</para>
/// </summary>
public static class GlobalAdministrationTab
{
    /// <summary>The tab id — unchanged from the user-settings tab it replaces, so old links redirect.</summary>
    public const string TabId = "GlobalAdmin";

    /// <summary>The tab definition.</summary>
    internal static SettingsMenuItemDefinition Definition { get; } = new(
        Id: TabId,
        Label: "Global Administration",
        ContentBuilder: (host, stack, _) => Build(host, stack),
        Group: AdminAppNodeType.AdministrationGroup,
        Icon: FluentIcons.Shield(),
        GroupIcon: FluentIcons.Shield(),
        Order: 300,
        Keywords: ["global admin", "platform admin", "administration", "invites",
            "users", "onboarding", "system", "access", "grants"])
    {
        LabelKey = "settings.globalAdministration",
        GroupKey = AdminAppNodeType.AdministrationGroupKey,
    };

    private static UiControl Build(LayoutAreaHost host, StackControl stack)
        => stack
            .WithView(Controls.H2(host.Localize("globalAdmin.title")).WithStyle("margin: 0 0 8px 0;"))
            .WithView(Controls.Markdown(host.Localize("globalAdmin.intro")))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{AdminAppNodeType.Path}/_Access nodeType:AccessAssignment")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(true)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithCollapsibleSections(false)
                .WithSectionCounts(false)
                .WithItemArea(MeshNodeLayoutAreas.ThumbnailArea)
                .WithDisableNavigation()
                .WithReactiveMode(true)
                .WithMaxColumns(2))
            .WithView(Controls.Button(host.Localize("ui.plusAddAdmin"))
                .WithAppearance(Appearance.Accent)
                .WithStyle("align-self: flex-start; margin-top: 8px;")
                .WithClickAction((Action<UiActionContext>)(addCtx =>
                    AccessControlLayoutArea.ShowAddAssignmentDialog(addCtx, AdminAppNodeType.Path))));
}

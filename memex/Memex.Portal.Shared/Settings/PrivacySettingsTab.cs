using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab for the public privacy statement (<c>Admin/Privacy</c>, served at
/// <c>/privacy</c>). Gated to platform admins (<see cref="AdminMenuGate.IsPlatformAdmin"/>).
/// Edits the statement through the STANDARD node-bound markdown editor
/// (<see cref="MarkdownEditorControl.WithAutoSave"/> — the same path the Markdown node's own
/// editor uses; the node's content IS a <c>MarkdownContent</c>, so the whole-content auto-save is
/// the correct shape here). The node is created on first open, prefilled with the generic EU/CH
/// default statement (<see cref="PrivacyStatementNode.DefaultStatement"/>).
/// </summary>
public static class PrivacySettingsTab
{
    public const string TabId = "Privacy";

    public static MessageHubConfiguration AddPrivacySettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var tab = new GlobalSettingsMenuItemDefinition(
            Id: TabId,
            Label: "Privacy",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.Shield(),
            GroupIcon: FluentIcons.Shield(),
            Order: 330);

        return AdminMenuGate.IsPlatformAdmin(host)
            .Select(isAdmin => isAdmin
                ? (IReadOnlyList<GlobalSettingsMenuItemDefinition>)new[] { tab }
                : []);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Privacy statement").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "This statement is shown publicly at [/privacy](/privacy) — no login required — and is " +
            "what external app registrations (e.g. LinkedIn) link as the privacy policy URL. It " +
            "starts from a generic statement drafted for EU (GDPR) and Swiss (revFADP) law; edit it " +
            "below to match your deployment. Changes are saved automatically."));

        // Editor bound DIRECTLY to Admin/Privacy. EnsureExists (create-on-absent as System,
        // prefilled with the default statement) before binding so the editor binds to an existing
        // node; the current statement is read once off the live node stream (the admin viewer has
        // Admin-partition read by the IsPlatformAdmin gate above), then edits flow back through
        // the auto-save — the same shape as MarkdownEditLayoutArea.
        stack = stack.WithView((h, _) => PrivacyStatementNode
            .EnsureExists(h.Hub, h.Hub.ServiceProvider.GetService<AccessService>())
            .SelectMany(_ => h.Workspace.GetMeshNodeStream(PrivacyStatementNode.NodePath)
                .Where(node => node is not null)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10)))
            .Select(node => (UiControl?)new MarkdownEditorControl()
                .WithDocumentId(PrivacyStatementNode.NodePath)
                .WithValue(PrivacyStatementNode.ParseStatement(node!.Content, h.Hub.JsonSerializerOptions))
                .WithHeight("calc(100vh - 320px)")
                .WithMaxHeight("none")
                .WithPlaceholder("Write the privacy statement in markdown…")
                .WithAutoSave(h.Hub.Address.ToString(), PrivacyStatementNode.NodePath))
            .Catch<UiControl?, Exception>(ex =>
                Observable.Return((UiControl?)Controls.Markdown($"_Could not load the privacy statement: {ex.Message}_")))
            .StartWith((UiControl?)Controls.Markdown("_Loading privacy statement…_")));

        return stack;
    }
}

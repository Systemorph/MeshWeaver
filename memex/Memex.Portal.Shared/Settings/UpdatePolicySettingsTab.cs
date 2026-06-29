using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.SelfUpdate;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Admin settings tab for the platform auto-update strategy (<c>Admin/UpdatePolicy</c>). Gated to
/// platform admins (<see cref="AdminMenuGate.IsPlatformAdmin"/>). Edits the policy through the
/// STANDARD node-content editor bound directly to the node stream (the <c>Policy</c> enum renders as
/// a dropdown — no /data replica, no save subscription), shows the running version + the latest tag
/// the poller has seen, and offers a manual "apply now" for installs that can self-patch.
/// </summary>
public static class UpdatePolicySettingsTab
{
    public const string TabId = "UpdatePolicy";
    private const string ResultId = "updatePolicyResult";

    public static MessageHubConfiguration AddUpdatePolicySettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var tab = new GlobalSettingsMenuItemDefinition(
            Id: TabId,
            Label: "Updates",
            ContentBuilder: BuildContent,
            Group: "Administration",
            Icon: FluentIcons.ArrowSync(),
            GroupIcon: FluentIcons.Shield(),
            Order: 320);

        return AdminMenuGate.IsPlatformAdmin(host)
            .Select(isAdmin => isAdmin
                ? (IReadOnlyList<GlobalSettingsMenuItemDefinition>)new[] { tab }
                : []);
    }

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Platform updates").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "The platform self-updates per the strategy below. **Continuous** rolls to the newest build " +
            "(including build-numbered continuous builds); **Stable** rolls only to clean releases; " +
            "**None** disables auto-update. On Kubernetes the portal patches its own deployment; elsewhere " +
            "the latest version is surfaced for a manual update."));

        // Running version (the installed platform version baked into the binary).
        stack = stack.WithView(Controls.Markdown(
            $"**Running version:** `{ShippedReleaseSeed.InstalledPlatformVersion}`"));

        // Policy editor — bound DIRECTLY to Admin/UpdatePolicy. EnsureExists (create-on-absent, as
        // System) before binding so the editor binds to an existing node.
        stack = stack.WithView((h, _) => UpdatePolicyNodeType
            .EnsureExists(h.Hub, h.Hub.ServiceProvider.GetService<AccessService>(), UpdatePolicyKind.Continuous)
            .Select(_ => (UiControl?)MeshNodeContentEditorControl.ForType(
                UpdatePolicyNodeType.NodePath, typeof(UpdatePolicyContent)))
            .StartWith((UiControl?)Controls.Markdown("_Loading update policy…_")));

        // Live status: the latest available tag + when last checked.
        stack = stack.WithView((h, _) => h.Hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath)
            .Select(node => (UiControl?)Controls.Markdown(
                StatusMarkdown(UpdatePolicyNodeType.Parse(node, h.Hub.JsonSerializerOptions))))
            .StartWith((UiControl?)Controls.Markdown("")));

        // Manual apply (installs that can self-patch). Reads the latest tag, then patches via the Http pool.
        stack = stack.WithView(Controls.Button("Apply available update now")
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                var h = ctx.Host;
                var updater = h.Hub.ServiceProvider.GetService<IDeploymentUpdater>();
                if (updater is null || !updater.CanPatch)
                {
                    h.UpdateData(ResultId, "This install cannot self-patch (not running in Kubernetes).");
                    return Task.CompletedTask;
                }
                var pool = h.Hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http)
                           ?? IoPool.Unbounded;
                h.Hub.GetWorkspace().GetMeshNodeStream(UpdatePolicyNodeType.NodePath).Take(1).Subscribe(node =>
                {
                    var tag = UpdatePolicyNodeType.Parse(node, h.Hub.JsonSerializerOptions).LatestAvailableTag;
                    if (string.IsNullOrEmpty(tag))
                    {
                        h.UpdateData(ResultId, "No newer version has been detected yet.");
                        return;
                    }
                    pool.Invoke(ct => updater.PatchToVersionAsync(tag, ct)).Subscribe(
                        _ => h.UpdateData(ResultId, $"Rolling the platform to {tag}…"),
                        ex => h.UpdateData(ResultId, $"Update failed: {ex.Message}"));
                });
                return Task.CompletedTask;
            }));

        // Result line.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(ResultId)
            .Select(msg => (UiControl?)(string.IsNullOrEmpty(msg)
                ? Controls.Stack.WithWidth("100%")
                : Controls.Markdown(msg)))
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%")));

        return stack;
    }

    private static string StatusMarkdown(UpdatePolicyContent content) =>
        string.IsNullOrEmpty(content.LatestAvailableTag)
            ? "_No newer version detected yet._"
            : $"**Latest available:** `{content.LatestAvailableTag}`" +
              (content.CheckedAt is { } at ? $" _(checked {at:yyyy-MM-dd HH:mm} UTC)_" : "");
}

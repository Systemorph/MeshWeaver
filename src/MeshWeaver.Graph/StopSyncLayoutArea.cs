using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// "Stop synchronization" / "Resume synchronization" toggle — flips a node's
/// <see cref="MeshNode.SyncBehavior"/> so the static-repo import leaves it (and its subtree)
/// alone. This is how a user CLAIMS an imported node: once stopped, the next import won't
/// overwrite their edits. See <c>Doc/Architecture/StaticRepoImport.md</c>.
/// </summary>
public static class StopSyncLayoutArea
{
    /// <summary>Area name for the stop/resume-synchronization toggle action.</summary>
    public const string StopSyncArea = "StopSync";

    /// <summary>
    /// Returns the toggle menu item, or null when the caller can't write the node. Shown to
    /// callers with Update — or with <see cref="Permission.Sync"/>, the privileged sync-write
    /// permission, so a read-only catalog node (Agent/Model) can still be claimed by an admin.
    /// The label reflects the node's current state: synced → "Stop synchronization"; excluded →
    /// "Resume synchronization".
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(MeshNode? node, string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Update) && !perms.HasFlag(Permission.Sync))
            return null;
        var excluded = node is { SyncBehavior: not SyncBehavior.Include };
        return new(
            excluded ? "Resume synchronization" : "Stop synchronization",
            StopSyncArea,
            Icon: excluded ? "PlugConnected" : "PlugDisconnected",
            Order: 75,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, StopSyncArea));
    }

    /// <summary>
    /// Toggles the own node's <see cref="MeshNode.SyncBehavior"/>
    /// (<see cref="SyncBehavior.Include"/> ⇄ <see cref="SyncBehavior.ExcludeThisAndChildren"/>)
    /// and renders a confirmation. The menu re-projects off the live node stream, so the label
    /// flips once the write lands.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> StopSync(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(StopSyncLayoutArea));

        host.Hub.GetWorkspace().GetMeshNodeStream()
            .Update(n => n with
            {
                SyncBehavior = n.SyncBehavior == SyncBehavior.Include
                    ? SyncBehavior.ExcludeThisAndChildren
                    : SyncBehavior.Include
            })
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "StopSync failed for {Path}", hubPath));

        return Observable.Return<UiControl?>(BuildSimpleMessage(
            "Synchronization toggled",
            $"Toggled static-repo synchronization for <code>{System.Net.WebUtility.HtmlEncode(hubPath)}</code>. "
            + "When excluded, the next import leaves this node (and its children) untouched.",
            backHref));
    }

    private static UiControl BuildSimpleMessage(string title, string messageHtml, string backHref)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center; margin-bottom: 16px;")
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(backHref))
                .WithView(Controls.H2(title).WithStyle("margin: 0;")))
            .WithView(Controls.Html(
                $"<p style=\"color: var(--neutral-foreground-hint);\">{messageHtml}</p>"));
}

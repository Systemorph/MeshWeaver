using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.GitSync;

/// <summary>
/// DI-registered <see cref="INodeMenuProvider"/> that contributes the Space's one-click GITHUB
/// actions to their OWN "GitHub" dropdown — shown only when the containing Space has a configured
/// <c>_GitSync</c> source (a repository URL) and the viewer may TRIGGER a sync: Read on the Space
/// (or platform admin) shows Check branch / Update to latest, Update (or platform admin) adds
/// Sync now — the same predicate the trigger itself enforces
/// (<c>GitHubActivityExtensions.TriggerAuthorizedAsSystem</c>). One submenu per source (its name),
/// gated by the source's <see cref="SyncDirection"/>; each navigates to
/// <see cref="GitHubActionArea"/>. Setup (repo / branch / direction / account connect) and the
/// input-driven flows (re-import at a commit, the PR workflow) stay in the settings tab.
/// </summary>
public sealed class GitHubSyncMenuProvider : INodeMenuProvider
{
    /// <inheritdoc />
    public string Context => NodeMenuItemsExtensions.GitHubMenuContext;

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> GetItems(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var spacePath = hubPath.Split('/', 2)[0];
        if (string.IsNullOrEmpty(spacePath))
            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>([]);

        var sync = host.Hub.ServiceProvider.GetService<GitHubSyncService>();
        if (sync is null)
            return Observable.Return<IReadOnlyCollection<NodeMenuItemDefinition>>([]);

        return sync.WatchConfigNodes(spacePath).StartWith([])
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spacePath),
                host.Hub.IsGlobalAdmin(),
                (nodes, perms, isAdmin) =>
                {
                    // Visibility mirrors the TRIGGER authorization (GitHubActivityExtensions.
                    // TriggerAuthorizedAsSystem): a GitSynced Space is SYSTEM-OWNED, so no real
                    // principal holds Update there — gating the menu on Update hid it from
                    // everyone the trigger now legitimately serves. Check/Update need Read on
                    // the Space; Sync now (commit) needs Update; a platform admin may do any op
                    // (an Admin-partition capability, never a per-space grant).
                    var mayTrigger = isAdmin || perms.HasFlag(Permission.Read);
                    if (!mayTrigger)
                        return (IReadOnlyCollection<NodeMenuItemDefinition>)[];
                    var mayCommit = isAdmin || perms.HasFlag(Permission.Update);

                    var items = new List<NodeMenuItemDefinition>();
                    var order = 0;
                    foreach (var node in nodes)
                    {
                        var cfg = node.ContentAs<GitHubSyncConfig>(host.Hub.JsonSerializerOptions);
                        if (cfg?.RepositoryUrl is not { Length: > 0 })
                            continue;   // repo not set yet — nothing to act on

                        var sourceId = string.Equals(node.Id, GitHubSyncService.ConfigId, StringComparison.Ordinal)
                            ? null : node.Id;
                        var canExport = cfg.Direction != SyncDirection.ImportOnly;
                        var canImport = cfg.Direction != SyncDirection.ExportOnly;

                        var children = new List<NodeMenuItemDefinition>();
                        var childOrder = 0;
                        // Icons are emoji (NodeMenuItemDefinition.Icon = emoji or SVG URL) — a Fluent
                        // icon NAME would render as a broken <img src="Name"> in both clients.
                        if (canExport && mayCommit)
                            children.Add(new NodeMenuItemDefinition("Sync now", GitHubActionArea.AreaName,
                                Icon: "⬆️", Order: childOrder++,
                                Href: GitHubActionArea.Href(hubPath, sourceId, GitHubActionArea.Commit))
                                { LabelKey = "menu.syncNow" });
                        if (canImport)
                            children.Add(new NodeMenuItemDefinition("Update to latest", GitHubActionArea.AreaName,
                                Icon: "⬇️", Order: childOrder++,
                                Href: GitHubActionArea.Href(hubPath, sourceId, GitHubActionArea.Update))
                                { LabelKey = "menu.updateToLatest" });
                        children.Add(new NodeMenuItemDefinition("Check branch", GitHubActionArea.AreaName,
                            Icon: "🔍", Order: childOrder++,
                            Href: GitHubActionArea.Href(hubPath, sourceId, GitHubActionArea.Check))
                            { LabelKey = "menu.checkBranch" });

                        items.Add(new NodeMenuItemDefinition(
                            Label: node.Name ?? sourceId ?? "GitHub",
                            Area: GitHubActionArea.AreaName,
                            Icon: "🐙",
                            Order: order++,
                            Tooltip: RepoSummary(cfg),
                            Children: children));
                    }
                    return items;
                });
    }

    private static string RepoSummary(GitHubSyncConfig cfg)
    {
        var repo = cfg.RepositoryUrl!.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        var branch = string.IsNullOrWhiteSpace(cfg.Branch) ? "main" : cfg.Branch;
        return $"{repo}@{branch} ({cfg.Direction})";
    }
}

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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// The "Instance Sync" settings tab — the GUI for syncing a Space with another MeshWeaver
/// instance. Appears on the Settings page of every node within a Space (always acting on the
/// containing Space, exactly like the GitHub Sync tab) for users with Update on the Space.
/// Lists every syncing party live (status, pending changes, last sync), binds the standard
/// node-content editor to each registration's config node (remote URL / token / target space /
/// direction / active — data-bound, <c>stream.Update</c>-persisting, nothing hand-rolled),
/// and offers Sync now / Remove per party plus the add-party form.
/// </summary>
public static class InstanceSyncSettingsTab
{
    /// <summary>The settings-menu item id for the Instance Sync tab.</summary>
    public const string TabId = "InstanceSync";

    /// <summary>Registers the Instance Sync settings tab provider (shown on any node within a Space).</summary>
    public static MessageHubConfiguration AddInstanceSyncSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();
        var tab = new SettingsMenuItemDefinition(
            Id: TabId,
            Label: "Instance Sync",
            ContentBuilder: BuildContent,
            Group: "Integration",
            Icon: FluentIcons.ArrowSync(),
            GroupIcon: FluentIcons.Document(),
            Order: 255,
            Keywords: ["instance sync", "sync", "remote instance", "replicate", "replication",
                "mirror", "bidirectional", "federation", "share", "memex"],
            // Visibility + the Update check are gated on the CONTAINING SPACE below, same as
            // the GitHub Sync tab — the feature replicates the whole Space.
            RequiredPermission: Permission.None);

        var spaceRoot = SpaceRootPath(host.Hub.Address.ToString());
        if (string.IsNullOrEmpty(spaceRoot))
            return Observable.Return(none);

        return host.Workspace.GetMeshNodeStream(spaceRoot)
            .Select(node => string.Equals(node?.NodeType, InstanceSyncService.SpaceNodeType, StringComparison.Ordinal))
            .CombineLatest(
                host.Hub.GetEffectivePermissions(spaceRoot),
                (isSpace, perms) => isSpace && perms.HasFlag(Permission.Update))
            .DistinctUntilChanged()
            .Select(show => show ? (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab } : none)
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    /// <summary>The partition root for a node path — its first segment (spaces are top-level).</summary>
    private static string SpaceRootPath(string? path) =>
        string.IsNullOrEmpty(path) ? "" : path.Split('/', 2)[0];

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var sp = host.Hub.ServiceProvider;
        var sync = sp.GetRequiredService<InstanceSyncService>();
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceSyncSettingsTab));
        var spacePath = SpaceRootPath(node?.Path ?? "");

        if (string.IsNullOrEmpty(spacePath))
            return stack.WithView(Controls.Markdown("*Instance Sync is available inside a Space.*"));

        stack = stack.WithView(Controls.H2("Instance Sync").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "Replicate this Space to another MeshWeaver instance and keep syncing changes as they "
            + "happen — like syncing a SharePoint library. Enter the remote instance's URL and an "
            + "API token issued there; the initial replication runs automatically, then changes flow "
            + "in the configured direction. If the remote is unreachable, changes accumulate and "
            + "sync as soon as it can be reached again."));

        // The live parties list: re-renders on add / remove / any config or status change.
        stack = stack.WithView((h, _) => sync.WatchConfigNodes(spacePath)
            .Select(sources => (UiControl?)BuildPartiesView(h, sync, spacePath, sources, logger))
            .StartWith((UiControl?)Controls.Markdown("*Loading sync parties…*")));

        return stack;
    }

    private static UiControl BuildPartiesView(
        LayoutAreaHost host, InstanceSyncService sync, string spacePath,
        IReadOnlyList<MeshNode> sources, ILogger? logger)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;");
        stack = stack.WithView(Controls.Title("Syncing parties", 3));

        if (sources.Count == 0)
            stack = stack.WithView(Controls.Markdown("*No syncing parties yet — add one below.*"));

        foreach (var source in sources)
            stack = stack.WithView(BuildPartyCard(sync, spacePath, source, logger));

        stack = stack.WithView(BuildAddButton(sync, spacePath, sources, logger));
        return stack;
    }

    private static UiControl BuildPartyCard(
        InstanceSyncService sync, string spacePath, MeshNode source, ILogger? logger)
    {
        var config = sync.Extract(source) ?? new InstanceSyncConfig();
        var card = Controls.Stack.WithWidth("100%")
            .WithStyle("padding: 12px; background: var(--neutral-layer-2); border-radius: 8px; gap: 8px;");

        card = card.WithView(Controls.Markdown(
            $"**{source.Name ?? source.Id}** — {InstanceSyncPartitionSyncSourceProvider.Describe(config)}"));
        card = card.WithView(Controls.Markdown(StatusLine(config)));

        // The connection settings — the STANDARD node-content editor bound directly to the
        // config node's stream (per-field auto-persisting; no replica, no save subscription).
        card = card.WithView(MeshNodeContentEditorControl.ForType(source.Path, typeof(InstanceSyncConfig)));

        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("flex-wrap: wrap;");

        // "Sync now" stamps the SyncRequestedAt control-plane field: the coordinator reacts to
        // the config change event and pokes the worker — works regardless of which process
        // renders this view (the standard Requested-field pattern).
        var sourcePath = source.Path;
        actions = actions.WithView(Controls.Button("Sync now")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowSync())
            .WithClickAction(ctx =>
            {
                sync.UpdateConfig(sourcePath, c => c with { SyncRequestedAt = DateTimeOffset.UtcNow })
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex, "Sync-now request failed for {Path}", sourcePath));
                return Task.CompletedTask;
            }));

        actions = actions.WithView(Controls.Button("Remove (stop syncing)")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Delete())
            .WithStyle("color: var(--error, #d32f2f);")
            .WithClickAction(ctx =>
            {
                // The parties list live-binds to WatchSyncSources — the delete re-emits and the
                // card disappears; the coordinator stops the worker on the same event.
                sync.RemoveSyncSource(spacePath, source.Id)
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex, "Removing sync party {Path} failed", sourcePath));
                return Task.CompletedTask;
            }));

        return card.WithView(actions);
    }

    private static string StatusLine(InstanceSyncConfig config)
    {
        var parts = new List<string> { $"- **Status:** {config.Status}" };
        if (config.PendingChanges.Count > 0)
            parts.Add($"- **Pending changes:** {config.PendingChanges.Count} (accumulate until the remote is reachable)");
        if (config.InitialSyncAt is { } initial)
            parts.Add($"- **Initial replication:** {initial:yyyy-MM-dd HH:mm} UTC");
        if (config.LastSyncedAt is { } last)
            parts.Add($"- **Last synced:** {last:yyyy-MM-dd HH:mm:ss} UTC");
        if (!string.IsNullOrEmpty(config.LastError))
            parts.Add($"- **Last error:** {config.LastError}");
        return string.Join("\n", parts);
    }

    /// <summary>
    /// One-click add: creates the registration under a generated unique id — the connection
    /// (URL, token, target space, direction) is then filled in through the standard node
    /// editor on the new card. No hand-rolled form state.
    /// </summary>
    private static UiControl BuildAddButton(
        InstanceSyncService sync, string spacePath, IReadOnlyList<MeshNode> sources, ILogger? logger)
    {
        var name = NextPartyName(sources);
        return Controls.Button("Add syncing party")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Add())
            .WithClickAction(ctx =>
            {
                // The parties list live-binds to WatchSyncSources; the create re-emits and the
                // new card (with its editor) appears on its own.
                sync.AddSyncSource(spacePath, name)
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex,
                            "Adding sync party '{Name}' to {Space} failed", name, spacePath));
                return Task.CompletedTask;
            });
    }

    /// <summary>First free "party-N" id given the existing sources.</summary>
    private static string NextPartyName(IReadOnlyList<MeshNode> sources)
    {
        var existing = sources.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (existing.Contains($"party-{n}"))
            n++;
        return $"party-{n}";
    }
}

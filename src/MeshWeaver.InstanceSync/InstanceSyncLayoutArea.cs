using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// The "Synchronizations" node-menu area — the GUI for syncing a Space with other MeshWeaver
/// instances. Reached from the node "⋯" menu (a per-node operation, alongside Files / Versions),
/// NOT from a settings tab. Lists every syncing party live (status, pending changes, last sync),
/// binds the standard node-content editor to each registration's config node (remote URL / token /
/// target space / direction / active — data-bound, <c>stream.Update</c>-persisting, nothing
/// hand-rolled), and offers Sync now / Pause·Resume / Remove per party plus the add-party form —
/// setup AND actions together in one place.
/// </summary>
public static class InstanceSyncLayoutArea
{
    /// <summary>Area name for the instance-sync management view (the "Synchronizations" menu item).</summary>
    public const string AreaName = "Sync";

    /// <summary>Renders the sync management view for the containing Space.</summary>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var spacePath = InstanceSyncService.SpaceOf(host.Hub.Address.ToString());
        return string.IsNullOrEmpty(spacePath)
            ? Observable.Return<UiControl?>(Controls.Markdown("*Instance Sync is available inside a Space.*"))
            : Observable.Return<UiControl?>(BuildContent(host, spacePath));
    }

    private static UiControl BuildContent(LayoutAreaHost host, string spacePath)
    {
        var sp = host.Hub.ServiceProvider;
        var sync = sp.GetRequiredService<InstanceSyncService>();
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(InstanceSyncLayoutArea));

        var stack = Controls.Stack
            .WithView(Controls.H2("Instance Sync").WithStyle("margin: 0 0 8px 0;"))
            .WithView(Controls.Markdown(
                "Replicate this Space to another MeshWeaver instance and keep syncing changes as they "
                + "happen — like syncing a SharePoint library. Enter the remote instance's URL and an "
                + "API token issued there; the initial replication runs automatically, then changes flow "
                + "in the configured direction. If the remote is unreachable, changes accumulate and "
                + "sync as soon as it can be reached again."));

        // The live parties list: re-renders on add / remove / any config or status change.
        return stack.WithView((h, _) => sync.WatchConfigNodes(spacePath)
            .Select(sources => (UiControl?)BuildPartiesView(h, sync, spacePath, sources, logger))
            .StartWith((UiControl?)Controls.Markdown("*Loading sync parties…*")));
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

        // OAuth alternative to pasting a token: full-page redirect to the remote instance's OWN
        // login (/connect/instance → the remote's /authorize), then the callback stores the
        // returned mw_ token as RemoteToken. Needs the Remote URL above to be set first (the
        // endpoint reads it server-side and redirects back with a reason if it's blank).
        var returnPath = MeshNodeLayoutAreas.BuildUrl(spacePath, AreaName);
        var connectHref = "/connect/instance"
            + $"?spaceId={Uri.EscapeDataString(spacePath)}"
            + $"&sourceId={Uri.EscapeDataString(source.Id)}"
            + $"&returnPath={Uri.EscapeDataString(returnPath)}";
        card = card.WithView(Controls.Button("Connect with the remote's login (OAuth)")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.PlugConnected())
            .WithNavigateToHref(connectHref));

        var sourcePath = source.Path;
        var configPath = InstanceSyncService.ConfigPath(spacePath, source.Id);
        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("flex-wrap: wrap;");

        // Sync now — flip the control-plane trigger; the worker drains on the next tick.
        actions = actions.WithView(Controls.Button("Sync now")
            .WithAppearance(Appearance.Accent)
            .WithIconStart(FluentIcons.ArrowSync())
            .WithClickAction(ctx =>
            {
                sync.UpdateConfig(configPath, c => c with { SyncRequestedAt = DateTimeOffset.UtcNow })
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex, "Sync-now for {Path} failed", configPath));
                return Task.CompletedTask;
            }));

        // Pause / Resume — toggle the Active flag; the coordinator starts/stops the worker.
        actions = actions.WithView(Controls.Button(config.Active ? "Pause" : "Resume")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(config.Active ? FluentIcons.Pause() : FluentIcons.Play())
            .WithClickAction(ctx =>
            {
                sync.UpdateConfig(configPath, c => c with { Active = !c.Active })
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex, "Pause/resume for {Path} failed", configPath));
                return Task.CompletedTask;
            }));

        actions = actions.WithView(Controls.Button("Remove (stop syncing)")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Delete())
            .WithStyle("color: var(--error, #d32f2f);")
            .WithClickAction(ctx =>
            {
                // The parties list live-binds to WatchConfigNodes — the delete re-emits and the
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
                // The parties list live-binds to WatchConfigNodes; the create re-emits and the
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

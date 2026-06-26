using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Platform-admin "Partition Sync" overview. Lists every syncable partition (one per
/// distinct <see cref="IStaticRepoSource.Partition"/>) with its source type(s) and live
/// sync status, and lets a global admin flip each partition between <b>Synced</b> and
/// <b>Not synced</b>. "Not synced" sets the partition ROOT's
/// <see cref="MeshNode.SyncBehavior"/> to <see cref="SyncBehavior.ExcludeThisAndChildren"/>,
/// which decouples the whole partition from static-repo import — e.g. so admin-managed API
/// keys in the <c>Provider</c> partition are never reset by the next import. See
/// <c>Doc/Architecture/StaticRepoImport.md</c> and <see cref="StopSyncLayoutArea"/>.
/// </summary>
public static class PartitionSyncAdminLayoutArea
{
    /// <summary>Area name for the partition-sync overview.</summary>
    public const string PartitionSyncArea = "PartitionSync";

    private const string SettingsTabId = "PartitionSync";

    /// <summary>
    /// One syncable partition: its namespace/root id plus a human-readable list of the
    /// <see cref="IStaticRepoSource"/> implementation type name(s) that materialize into it.
    /// </summary>
    private sealed record PartitionInfo(string Partition, string Source);

    /// <summary>
    /// Plain row record bound into the <see cref="DataGridControl"/> (camelCased property names).
    /// </summary>
    public sealed record PartitionSyncRow
    {
        /// <summary>The partition namespace/root id.</summary>
        public string Partition { get; init; } = string.Empty;
        /// <summary>Human-readable list of the source type name(s) that materialize into the partition.</summary>
        public string Source { get; init; } = string.Empty;
        /// <summary>The partition's current sync status ("Synced" or "Not synced").</summary>
        public string Status { get; init; } = string.Empty;
    }

    /// <summary>
    /// Registers a global-admin "Partition Sync" tab on the node's Settings page. The provider
    /// only yields the tab once the viewer is confirmed a global admin (Admin-partition
    /// <see cref="MeshWeaver.Mesh.Security.Permission.All"/>); the tab content embeds the reactive
    /// <see cref="PartitionSyncOverview"/> area, which gates again as defense-in-depth.
    /// </summary>
    public static MessageHubConfiguration AddPartitionSyncSettingsTab(this MessageHubConfiguration config)
        => config.AddSettingsMenuItems(new SettingsMenuItemProvider(GetPartitionSyncTab));

    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> GetPartitionSyncTab(
        LayoutAreaHost host, RenderingContext ctx)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = Array.Empty<SettingsMenuItemDefinition>();

        var viewerId = ResolveViewerId(host);
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return(none);

        var tab = new SettingsMenuItemDefinition(
            Id: SettingsTabId,
            Label: "Partition Sync",
            ContentBuilder: BuildPartitionSyncTab,
            Group: "Administration",
            Icon: FluentIcons.ArrowSync(),
            GroupIcon: FluentIcons.Shield(),
            Order: 310,
            Keywords: ["partition sync", "sync", "static repo", "import", "synced",
                "not synced", "decouple", "api keys", "provider", "administration", "platform"]);

        // Same shape as the Global Administration tab: wait for the POSITIVE admin confirmation
        // (filter true) with a bounded timeout — NOT the first emission, which can be a premature
        // false before the synced AccessAssignment query lands. StartWith(none) renders the menu
        // immediately; the tab appears once admin is confirmed. Timeout/non-admin → stays hidden.
        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)new[] { tab })
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    /// <summary>
    /// Settings-tab content: embeds the reactive <see cref="PartitionSyncOverview"/> area. The
    /// embedded observable stays live (CombineLatest over the partition root streams), so the
    /// status column + toggle labels update as soon as a write lands.
    /// </summary>
    private static UiControl BuildPartitionSyncTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
        => stack.WithView(BuildArea(host));

    /// <summary>
    /// The reactive Partition Sync area, registrable via
    /// <c>AddLayout(layout =&gt; layout.WithView(PartitionSyncArea, PartitionSyncOverview))</c>.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> PartitionSyncOverview(LayoutAreaHost host, RenderingContext _)
        => BuildArea(host);

    /// <summary>
    /// Gates to global admins (reactively), then renders the live overview. Non-admins (or a
    /// gate timeout) get an "Access denied" message — never a blocking call.
    /// </summary>
    private static IObservable<UiControl?> BuildArea(LayoutAreaHost host)
    {
        var viewerId = ResolveViewerId(host);
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return<UiControl?>(AccessDenied());

        // Wait for the positive admin confirmation (filter true) with a bounded timeout, exactly
        // as the Global Administration tab does. No true within the window (or an error) → treat
        // as not-admin and show Access denied. A confirmed admin → the live overview.
        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => true)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .Select(isAdmin => isAdmin
                ? BuildAdminView(host)
                : Observable.Return<UiControl?>(AccessDenied()))
            .Switch();
    }

    private static UiControl AccessDenied()
        => Controls.Markdown("Access denied — platform admins only.");

    private static IObservable<UiControl?> BuildAdminView(LayoutAreaHost host)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PartitionSyncAdminLayoutArea));

        // Distinct syncable partitions, grouped so a partition served by several sources shows
        // all their type names. Ordered for a stable layout.
        var partitions = host.Hub.ServiceProvider.GetServices<IStaticRepoSource>()
            .GroupBy(s => s.Partition, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PartitionInfo(
                g.Key,
                string.Join(", ", g.Select(s => s.GetType().Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal))))
            .ToImmutableArray();

        if (partitions.Length == 0)
            return Observable.Return<UiControl?>(Controls.Stack
                .WithView(Controls.Title("Partition Sync", 1))
                .WithView(Controls.Markdown("*No syncable partitions are registered.*")));

        // Live: one stream per partition ROOT (namespace="", path==partition). Each starts with
        // null so CombineLatest fires immediately (optimistic "Synced") and updates as roots load
        // and as toggles land.
        var streams = partitions.Select(p =>
            host.Workspace.GetMeshNodeStream(p.Partition)
                .Select(n => (MeshNode?)n)
                .StartWith((MeshNode?)null));

        return Observable.CombineLatest(streams)
            .Select(nodes =>
            {
                var rows = partitions
                    .Select((p, i) => new PartitionSyncRow
                    {
                        Partition = p.Partition,
                        Source = p.Source,
                        Status = IsSynced(nodes[i]) ? "Synced" : "Not synced",
                    })
                    .ToImmutableArray();

                return (UiControl?)BuildView(host, rows, logger);
            });
    }

    // Anything other than the whole-subtree exclusion is treated as Synced (null/Include/ThisOnly).
    private static bool IsSynced(MeshNode? node)
        => node?.SyncBehavior != SyncBehavior.ExcludeThisAndChildren;

    private static UiControl BuildView(
        LayoutAreaHost host, ImmutableArray<PartitionSyncRow> rows, ILogger? logger)
    {
        var id = Guid.NewGuid().ToString();
        host.RegisterForDisposal(Observable.Return(rows).Subscribe(data => host.UpdateData(id, data)));

        var grid = Controls.DataGrid(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Partition).ToCamelCase() }.WithTitle("Partition"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Source).ToCamelCase() }.WithTitle("Source"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Status).ToCamelCase() }.WithTitle("Status"))
            .Resizable();

        // DataGrid has no row-click, so render one toggle button per partition next to the grid.
        var buttonRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("flex-wrap: wrap;");
        foreach (var row in rows)
            buttonRow = buttonRow.WithView(BuildToggleButton(row, logger));

        return Controls.Stack
            .WithView(Controls.Title("Partition Sync", 1))
            .WithView(Controls.Markdown(
                "Toggle each partition between **Synced** and **Not synced**. "
                + "Setting a partition to **Not synced** excludes its whole subtree from "
                + "static-repo import, so admin-managed content (e.g. API keys) is never reset."))
            .WithView(grid)
            .WithView(Controls.Title("Toggle synchronization", 2))
            .WithView(buttonRow);
    }

    private static UiControl BuildToggleButton(PartitionSyncRow row, ILogger? logger)
    {
        var synced = row.Status == "Synced";
        var partitionPath = row.Partition;
        return Controls.Button(synced
                ? $"Set Not synced: {row.Partition}"
                : $"Set Synced: {row.Partition}")
            .WithAppearance(synced ? Appearance.Lightweight : Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.Workspace.GetMeshNodeStream(partitionPath)
                    .Update(n => n with
                    {
                        SyncBehavior = n.SyncBehavior == SyncBehavior.Include
                            ? SyncBehavior.ExcludeThisAndChildren
                            : SyncBehavior.Include
                    })
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex, "Partition sync toggle failed for {Path}", partitionPath));
                return Task.CompletedTask;
            });
    }

    private static string? ResolveViewerId(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context?.ObjectId
               ?? accessService?.CircuitContext?.ObjectId;
    }
}

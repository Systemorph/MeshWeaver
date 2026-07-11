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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Platform-admin "Partitions" overview. Lists EVERY partition in the mesh — one row per
/// partition ROOT node (namespace empty, id = partition) — with the main node's properties,
/// its sync status, and its sync sources. From here a global admin can:
/// <list type="bullet">
///   <item>flip a partition between <b>Synced</b> and <b>Not synced</b> ("Not synced" sets the
///     root's <see cref="MeshNode.SyncBehavior"/> to <see cref="SyncBehavior.ExcludeThisAndChildren"/>,
///     decoupling the whole partition from static-repo import — e.g. so admin-managed API keys
///     in the <c>Provider</c> partition are never reset; see <c>Doc/Architecture/StaticRepoImport.md</c>);</item>
///   <item>edit each sync source's settings in place (repository, branch,
///     <b>sync direction</b> — bidirectional / export-only / import-only) through the standard
///     node-content editor, bound DIRECTLY to the source's config node stream;</item>
///   <item>add / remove sync sources (via the <see cref="IPartitionSyncSourceProvider"/> seam —
///     e.g. GitHub sources from <c>MeshWeaver.GitSync</c>);</item>
///   <item>navigate to the partition, or delete the space (which drops the ENTIRE partition —
///     see <c>PartitionDropPostDeletionHandler</c>).</item>
/// </list>
/// </summary>
public static class PartitionSyncAdminLayoutArea
{
    /// <summary>Area name for the partitions overview.</summary>
    public const string PartitionSyncArea = "PartitionSync";

    private const string SettingsTabId = "PartitionSync";

    /// <summary>Data id holding the currently selected partition (the detail panel binds to it).</summary>
    private const string SelectedPartitionId = "partitionsOverviewSelected";

    /// <summary>Data id of the add-sync-source form input.</summary>
    private const string AddSourceFormId = "partitionsOverviewAddSource";

    /// <summary>
    /// One partition: its namespace/root id plus a human-readable list of the
    /// <see cref="IStaticRepoSource"/> implementation type name(s) that materialize into it
    /// (empty for partitions without a static source).
    /// </summary>
    private sealed record PartitionInfo(string Partition, string StaticSource);

    /// <summary>
    /// Plain row record bound into the <see cref="DataGridControl"/> (camelCased property names).
    /// </summary>
    public sealed record PartitionSyncRow
    {
        /// <summary>The partition namespace/root id.</summary>
        public string Partition { get; init; } = string.Empty;
        /// <summary>The partition main node's display name.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>The partition main node's node type (e.g. "Space").</summary>
        public string Type { get; init; } = string.Empty;
        /// <summary>Who created the partition main node.</summary>
        public string CreatedBy { get; init; } = string.Empty;
        /// <summary>The partition's current sync status ("Synced" or "Not synced").</summary>
        public string Status { get; init; } = string.Empty;
        /// <summary>Summary of the partition's sync sources (static repo + configured git sources with direction).</summary>
        public string Sources { get; init; } = string.Empty;
    }

    /// <summary>
    /// Registers the global-admin "Partitions" tab on the node's Settings page. The provider
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
            Label: "Partitions",
            ContentBuilder: BuildPartitionSyncTab,
            Group: "Administration",
            Icon: FluentIcons.ArrowSync(),
            GroupIcon: FluentIcons.Shield(),
            Order: 310,
            Keywords: ["partitions", "partition overview", "spaces", "partition sync", "sync", "sync source",
                "sync direction", "static repo", "import", "export", "synced", "not synced", "decouple",
                "api keys", "provider", "administration", "platform", "delete space"]);

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
    /// grid + detail panel update as soon as a write lands.
    /// </summary>
    private static UiControl BuildPartitionSyncTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
        => stack.WithView(BuildArea(host));

    /// <summary>
    /// The reactive Partitions area, registrable via
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

    // ── Partition discovery ────────────────────────────────────────────────────

    /// <summary>
    /// Discovers every partition: the union of all partition ROOT nodes (top-level
    /// <c>Space</c> nodes — every space, user-created or importer-created, is one) and the
    /// partitions registered by an <see cref="IStaticRepoSource"/> (present even before
    /// their first import materializes the root). Ordered for a stable layout.
    /// </summary>
    private static IObservable<ImmutableArray<PartitionInfo>> DiscoverPartitions(LayoutAreaHost host)
    {
        // Case-insensitive throughout — partition ids dedupe/order case-insensitively below,
        // so a Space root whose casing differs from IStaticRepoSource.Partition must still
        // resolve its static-source names.
        var staticSources = host.Hub.ServiceProvider.GetServices<IStaticRepoSource>()
            .GroupBy(s => s.Partition, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(s => s.GetType().Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal)),
                StringComparer.OrdinalIgnoreCase);

        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        var spaceRoots = meshService is null
            ? Observable.Return(new List<MeshNode>())
            : meshService.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Space"))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Select(change => change.Items.Where(n => string.IsNullOrEmpty(n.Namespace)).ToList())
                .Timeout(TimeSpan.FromSeconds(10))
                .Catch<List<MeshNode>, Exception>(_ => Observable.Return(new List<MeshNode>()));

        return spaceRoots.Select(spaces => spaces
            .Select(n => n.Id)
            .Concat(staticSources.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PartitionInfo(p, staticSources.GetValueOrDefault(p, "")))
            .ToImmutableArray());
    }

    private static IObservable<UiControl?> BuildAdminView(LayoutAreaHost host)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(PartitionSyncAdminLayoutArea));
        var providers = host.Hub.ServiceProvider.GetServices<IPartitionSyncSourceProvider>().ToList();

        return DiscoverPartitions(host).Select(partitions =>
        {
            if (partitions.Length == 0)
                return Observable.Return<UiControl?>(Controls.Stack
                    .WithView(Controls.Title("Partitions", 1))
                    .WithView(Controls.Markdown("*No partitions found.*")));

            // Live: one row stream per partition — the ROOT node stream (namespace="",
            // path==partition; starts with null so CombineLatest fires immediately) combined
            // with the partition's sync-source summary.
            var rowStreams = partitions.Select(p => host.Workspace
                .GetMeshNodeStream(p.Partition)
                .Select(n => (MeshNode?)n)
                .StartWith((MeshNode?)null)
                .CombineLatest(
                    SourcesSummary(providers, p.Partition, p.StaticSource),
                    (node, sources) => new PartitionSyncRow
                    {
                        Partition = p.Partition,
                        Name = node?.Name ?? p.Partition,
                        Type = node?.NodeType ?? "",
                        CreatedBy = node?.CreatedBy ?? "",
                        Status = StatusOf(node),
                        Sources = sources,
                    }));

            return Observable.CombineLatest(rowStreams)
                .Select(rows => (UiControl?)BuildView(host, [.. rows], providers, logger));
        }).Switch();
    }

    /// <summary>
    /// One-line summary of the partition's sync sources: the static-repo source type name(s)
    /// plus each provider-managed source (e.g. GitHub repo@branch with its direction).
    /// </summary>
    private static IObservable<string> SourcesSummary(
        IReadOnlyList<IPartitionSyncSourceProvider> providers, string partition, string staticSource)
    {
        var staticPart = string.IsNullOrEmpty(staticSource) ? null : $"Static: {staticSource}";
        if (providers.Count == 0)
            return Observable.Return(staticPart ?? "—");

        var streams = providers.Select(p => p.WatchSyncSources(partition)
            .Select(nodes => nodes.Select(n => $"{p.Kind}: {p.Describe(n)}").ToList())
            .StartWith(new List<string>()));
        return Observable.CombineLatest(streams).Select(lists =>
        {
            var parts = new List<string>();
            if (staticPart is not null) parts.Add(staticPart);
            parts.AddRange(lists.SelectMany(l => l));
            return parts.Count == 0 ? "—" : string.Join(" · ", parts);
        });
    }

    // Anything other than the whole-subtree exclusion is treated as Synced (Include/ThisOnly).
    private static bool IsSynced(MeshNode? node)
        => node?.SyncBehavior != SyncBehavior.ExcludeThisAndChildren;

    /// <summary>
    /// The grid's Status cell. A partition registered by a source but whose ROOT node does not
    /// exist has never been imported/installed — that is "Not materialized", NOT "Synced" (the
    /// old two-state status showed "Synced" for a null root, claiming sync for content that was
    /// never mounted at all).
    /// </summary>
    private static string StatusOf(MeshNode? node) =>
        node is null ? "Not materialized"
        : IsSynced(node) ? "Synced"
        : "Not synced";

    // ── View composition ───────────────────────────────────────────────────────

    private static UiControl BuildView(
        LayoutAreaHost host, ImmutableArray<PartitionSyncRow> rows,
        IReadOnlyList<IPartitionSyncSourceProvider> providers, ILogger? logger)
    {
        var id = Guid.NewGuid().ToString();
        host.RegisterForDisposal(Observable.Return(rows).Subscribe(data => host.UpdateData(id, data)));

        var grid = Controls.DataGrid(new JsonPointerReference(LayoutAreaReference.GetDataPointer(id)))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Partition).ToCamelCase() }.WithTitle("Partition"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Name).ToCamelCase() }.WithTitle("Name"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Type).ToCamelCase() }.WithTitle("Type"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.CreatedBy).ToCamelCase() }.WithTitle("Created By"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Status).ToCamelCase() }.WithTitle("Status"))
            .WithColumn(new PropertyColumnControl<string>
                { Property = nameof(PartitionSyncRow.Sources).ToCamelCase() }.WithTitle("Sync Sources"))
            .Resizable();

        // DataGrid has no row-click, so render one select button per partition; the detail
        // panel below binds to the selection.
        var selectRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("flex-wrap: wrap;");
        foreach (var row in rows)
        {
            var partition = row.Partition;
            selectRow = selectRow.WithView(Controls.Button(partition)
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(SelectedPartitionId, partition);
                    // Reset the add-source input when switching partitions.
                    ctx.Host.UpdateData(AddSourceFormId, new Dictionary<string, object?> { ["name"] = "" });
                    return Task.CompletedTask;
                }));
        }

        var stack = Controls.Stack
            .WithView(Controls.Title("Partitions", 1))
            .WithView(Controls.Markdown(
                "Every partition in the mesh, with its main node (namespace empty, id = partition), "
                + "sync status and sync sources. Select a partition below to manage it: toggle "
                + "static-repo sync, edit each sync source's settings (repository, branch, "
                + "**sync direction** — bidirectional, export-only or import-only), add or remove "
                + "sync sources, or delete the space (which removes the entire partition)."))
            .WithView(grid)
            .WithView(Controls.Title("Manage partition", 2))
            .WithView(selectRow);

        // The detail panel — bound to the selected partition, live inner streams via Switch.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(SelectedPartitionId)
            .Select(partition => string.IsNullOrEmpty(partition)
                ? Observable.Return<UiControl?>(Controls.Markdown("*Select a partition to manage it.*"))
                : BuildDetailStream(h, partition, providers, logger))
            .Switch()
            .StartWith((UiControl?)Controls.Markdown("*Select a partition to manage it.*")));

        return stack;
    }

    /// <summary>
    /// The live detail panel for one partition: the main node's properties, the sync toggle +
    /// navigation/delete actions, and every sync source with its data-bound editor.
    /// </summary>
    private static IObservable<UiControl?> BuildDetailStream(
        LayoutAreaHost host, string partition,
        IReadOnlyList<IPartitionSyncSourceProvider> providers, ILogger? logger)
    {
        var root = host.Workspace.GetMeshNodeStream(partition)
            .Select(n => (MeshNode?)n)
            .StartWith((MeshNode?)null);

        var sourcesStreams = providers
            .Select(p => p.WatchSyncSources(partition)
                .Select(nodes => (Provider: p, Nodes: nodes))
                .StartWith((Provider: p, Nodes: (IReadOnlyList<MeshNode>)[])))
            .ToList();

        var sources = sourcesStreams.Count == 0
            ? Observable.Return(new List<(IPartitionSyncSourceProvider Provider, IReadOnlyList<MeshNode> Nodes)>())
            : Observable.CombineLatest(sourcesStreams).Select(x => x.ToList());

        return root.CombineLatest(sources,
            (node, sourceGroups) => (UiControl?)BuildDetailPanel(partition, node, sourceGroups, logger));
    }

    private static UiControl BuildDetailPanel(
        string partition, MeshNode? node,
        List<(IPartitionSyncSourceProvider Provider, IReadOnlyList<MeshNode> Nodes)> sourceGroups,
        ILogger? logger)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;");
        stack = stack.WithView(Controls.Title(node?.Name ?? partition, 3));

        // Main node properties (namespace empty, id = partition).
        stack = stack.WithView(Controls.Markdown(node is null
            ? $"*The partition root node `{partition}` is not materialized (yet).*"
            : $"- **Partition:** `{partition}`\n"
              + $"- **Name:** {node.Name ?? partition}\n"
              + $"- **Node type:** {node.NodeType}\n"
              + $"- **Created:** {node.CreatedDate:yyyy-MM-dd HH:mm} UTC by {node.CreatedBy ?? "—"}\n"
              + $"- **Last modified:** {node.LastModified:yyyy-MM-dd HH:mm} UTC by {node.LastModifiedBy ?? "—"}\n"
              + $"- **Sync behavior:** {node.SyncBehavior} ({(IsSynced(node) ? "Synced" : "Not synced")})"));

        // Actions: toggle static-repo sync, open the partition, delete the space.
        var actions = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("flex-wrap: wrap;");
        var synced = IsSynced(node);
        actions = actions.WithView(Controls.Button(synced ? "Set Not synced" : "Set Synced")
            .WithAppearance(synced ? Appearance.Lightweight : Appearance.Accent)
            .WithClickAction(ctx =>
            {
                ctx.Host.Workspace.GetMeshNodeStream(partition)
                    .Update(n => n with
                    {
                        SyncBehavior = n.SyncBehavior == SyncBehavior.Include
                            ? SyncBehavior.ExcludeThisAndChildren
                            : SyncBehavior.Include
                    })
                    .Subscribe(_ => { },
                        ex => logger?.LogWarning(ex, "Partition sync toggle failed for {Path}", partition));
                return Task.CompletedTask;
            }));
        actions = actions.WithView(Controls.Button("Open")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Open())
            .WithNavigateToHref($"/{partition}"));
        actions = actions.WithView(Controls.Button("Delete space…")
            .WithAppearance(Appearance.Outline)
            .WithIconStart(FluentIcons.Delete())
            .WithStyle("color: var(--error, #d32f2f);")
            .WithNavigateToHref(MeshNodeLayoutAreas.BuildUrl(partition, MeshNodeLayoutAreas.DeleteArea)));
        stack = stack.WithView(actions);

        // Sync sources — each config node edited through the STANDARD node-content editor,
        // bound directly to the node stream (no /data replica, no save subscription).
        foreach (var (provider, nodes) in sourceGroups)
        {
            stack = stack.WithView(Controls.Title($"{provider.Kind} sync sources", 4));
            if (nodes.Count == 0)
                stack = stack.WithView(Controls.Markdown("*No sync sources configured.*"));
            foreach (var source in nodes)
            {
                var sourceStack = Controls.Stack.WithWidth("100%")
                    .WithStyle("padding: 12px; background: var(--neutral-layer-2); border-radius: 8px; gap: 8px;");
                sourceStack = sourceStack.WithView(Controls.Markdown(
                    $"**{source.Name ?? source.Id}** — {provider.Describe(source)}"));
                sourceStack = sourceStack.WithView(
                    MeshNodeContentEditorControl.ForType(source.Path, provider.ConfigContentType));
                if (provider.CanRemove(partition, source))
                {
                    var capturedSource = source;
                    sourceStack = sourceStack.WithView(Controls.Button("Remove source")
                        .WithAppearance(Appearance.Outline)
                        .WithClickAction(ctx =>
                        {
                            // The detail panel live-binds to WatchSyncSources; the delete
                            // re-emits and the removed source disappears on its own.
                            provider.RemoveSyncSource(partition, capturedSource)
                                .Subscribe(_ => { },
                                    ex => logger?.LogWarning(ex,
                                        "Removing sync source {Path} failed", capturedSource.Path));
                            return Task.CompletedTask;
                        }));
                }
                stack = stack.WithView(sourceStack);
            }
            stack = stack.WithView(BuildAddSourceForm(provider, partition, logger));
        }

        return stack;
    }

    private static UiControl BuildAddSourceForm(
        IPartitionSyncSourceProvider provider, string partition, ILogger? logger)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("align-items: flex-end;");
        row = row.WithView(new TextFieldControl(new JsonPointerReference("name"))
        {
            Label = $"New {provider.Kind} sync source",
            Placeholder = "e.g. upstream, mirror, backup",
            DataContext = LayoutAreaReference.GetDataPointer(AddSourceFormId),
        }.WithWidth("280px"));
        row = row.WithView(Controls.Button("Add sync source")
            .WithAppearance(Appearance.Outline)
            .WithClickAction(ctx =>
            {
                ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(AddSourceFormId)
                    .Take(1)
                    .Subscribe(form =>
                    {
                        var name = form.GetValueOrDefault("name") is { } v ? v.ToString()?.Trim() : null;
                        if (string.IsNullOrEmpty(name))
                            return;
                        // The sources list live-binds to WatchSyncSources; the create re-emits
                        // and the new source's editor appears on its own.
                        provider.AddSyncSource(partition, name)
                            .Subscribe(_ => ctx.Host.UpdateData(AddSourceFormId,
                                    new Dictionary<string, object?> { ["name"] = "" }),
                                ex => logger?.LogWarning(ex,
                                    "Adding sync source '{Name}' to {Partition} failed", name, partition));
                    });
                return Task.CompletedTask;
            }));
        return row;
    }

    private static string? ResolveViewerId(LayoutAreaHost host)
    {
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        return accessService?.Context?.ObjectId
               ?? accessService?.CircuitContext?.ObjectId;
    }
}

using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>Outcome of a <see cref="StaticRepoImporter.Import"/> run.</summary>
public sealed record StaticRepoImportResult(string Partition, string Fingerprint, string Outcome, int Count = 0);

/// <summary>
/// Materializes an <see cref="IStaticRepoSource"/> into its partition through the canonical
/// create pipeline — content + prerender — tracked as a content-addressed <c>Activity</c> and
/// idempotent via the source fingerprint. See <c>Doc/Architecture/StaticRepoImport.md</c>.
///
/// <para>Single-execution: the activity at <c>{Partition}/_Activity/import-{fingerprint}</c> is the
/// lock — <see cref="IMeshService.CreateNode"/> makes the first caller win and concurrent replicas
/// get "already exists". A <see cref="ActivityStatus.Succeeded"/> activity for the fingerprint is
/// the durable "already imported" record (the short-circuit). Reactive end-to-end — no await.</para>
/// </summary>
public static class StaticRepoImporter
{
    private const int BatchSize = 16;

    /// <summary>
    /// "Sync context init" — imports EVERY registered <see cref="IStaticRepoSource"/> resolved from
    /// the hub. No-op when none is registered (so a host that registers no source is untouched).
    /// Runs under <see cref="AccessService.ImpersonateAsSystem"/> so the import's overwrite / create /
    /// prune are authorized on partitions whose <c>_Policy</c> is read-only to ordinary users:
    /// <see cref="RlsNodeValidator"/> short-circuits to Valid for the well-known System identity
    /// (it bypasses RLS entirely — it does NOT rely on a <see cref="MeshWeaver.Mesh.Security.Permission.Sync"/>
    /// grant, which the read-only <c>_Policy</c> cap would strip anyway). System-based sync is the
    /// intended mechanism for built-in static-repo content.
    /// Sources are imported sequentially (bounded boot load). Reactive — Subscribe to run.
    /// </summary>
    public static IObservable<StaticRepoImportResult> ImportAll(IMessageHub hub, ILogger? logger = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var sources = hub.ServiceProvider.GetServices<IStaticRepoSource>().ToArray();
        if (sources.Length == 0)
            return Observable.Empty<StaticRepoImportResult>();

        logger?.LogInformation("[StaticRepoImport] sync-context init: {Count} source(s).", sources.Length);
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        // Establish System identity for the whole import subscription so each source's writes
        // capture it (CarryAccessContext) — disposed when the import completes.
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
            _ => sources
                .Select(s => Import(hub, s, logger)
                    .Catch<StaticRepoImportResult, Exception>(ex =>
                    {
                        logger?.LogWarning(ex, "[StaticRepoImport] source {Partition} failed.", s.Partition);
                        return Observable.Return(new StaticRepoImportResult(s.Partition, string.Empty, "Failed"));
                    }))
                .Concat());
    }

    public static IObservable<StaticRepoImportResult> Import(
        IMessageHub hub, IStaticRepoSource source, ILogger? logger = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        var nodes = source.EnumerateSourceNodes();
        var fingerprint = PartitionSourceFingerprint.Compute(nodes, source.Versioned, hub.JsonSerializerOptions);
        var activityId = $"import-{fingerprint}";
        var activityNamespace = $"{source.Partition}/_Activity";
        var activityPath = $"{activityNamespace}/{activityId}";

        // Short-circuit: a Succeeded import activity for this fingerprint = already imported.
        // (Existence check via query — eventually consistent, but the CreateNode lock below is the
        // authoritative guard; a stale miss just attempts the create and loses the race.)
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{activityPath}"))
            .Take(1)
            .SelectMany(change =>
            {
                if (change.Items.FirstOrDefault()?.Content is ActivityLog { Status: ActivityStatus.Succeeded })
                {
                    logger?.LogInformation(
                        "[StaticRepoImport] {Partition} already at {Fingerprint} — skipping.",
                        source.Partition, fingerprint);
                    return Observable.Return(new StaticRepoImportResult(source.Partition, fingerprint, "Skipped"));
                }

                var activityNode = new MeshNode(activityId, activityNamespace)
                {
                    Name = $"Import {source.Partition} ({nodes.Count} nodes)",
                    NodeType = ActivityNodeType.NodeType,
                    MainNode = source.Partition,
                    State = MeshNodeState.Active,
                    Content = new ActivityLog(ActivityCategory.Import)
                    {
                        Id = activityId,
                        HubPath = source.Partition,
                        Status = ActivityStatus.Running
                    }
                };

                // CreateNode is the lock: first instance wins; concurrent replicas fault here.
                return meshService.CreateNode(activityNode)
                    .SelectMany(_ => Run(hub, source, nodes, activityPath, fingerprint, logger))
                    .Catch<StaticRepoImportResult, Exception>(ex =>
                    {
                        logger?.LogInformation(
                            "[StaticRepoImport] {Partition} ({Fingerprint}) lock held / create faulted: {Message}",
                            source.Partition, fingerprint, ex.Message);
                        return Observable.Return(
                            new StaticRepoImportResult(source.Partition, fingerprint, "AlreadyRunning"));
                    });
            });
    }

    private static IObservable<StaticRepoImportResult> Run(
        IMessageHub hub, IStaticRepoSource source, IReadOnlyList<MeshNode> nodes,
        string activityPath, string fingerprint, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        NodeTypeCompilationActivity.AppendLog(
            hub, activityPath, $"Importing {nodes.Count} node(s) into {source.Partition}…", logger!);

        // Read the existing target subtree(s) ONCE. A source's nodes may span MULTIPLE partitions
        // (e.g. the model catalog: the read-only _Policy under "Model", the provider/model content
        // under "_Provider"), so read each touched partition's subtree. The snapshot yields each
        // target's SyncBehavior (skip decision), its identity (CreatedDate/CreatedBy, preserved on
        // overwrite), and the prune candidate set. Eventual consistency is fine here — the import
        // runs under the content-addressed activity lock, so no concurrent import races this read.
        var partitions = nodes
            .Select(n => FirstSegment(n.Path))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 🚨 Reactively ensure each touched partition's storage (PG schema) exists BEFORE reading or
        // writing — via the STANDARD IPartitionStorageProvider.EnsurePartitionProvisioned, which is
        // reactive + POOLED (the PG impl runs CREATE SCHEMA on the pg pool and promise-caches it; the
        // schema name is lowercased correctly). Providers that don't own the partition no-op. This is
        // the canonical provisioning entry point — never declare PartitionDefinition nodes to force a
        // schema (that path used the namespace verbatim and provisioned the wrong CASE). No async,
        // no FromAsync — all IObservable. See Doc/Architecture/ControlledIoPooling.md + StaticRepoImport.md.
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var provisionLeaves = partitions
            .SelectMany(p => providers.Select(pr => pr.EnsurePartitionProvisioned(p)))
            .ToArray();
        var provision = provisionLeaves.Length == 0
            ? Observable.Return(System.Reactive.Unit.Default)
            : Observable.Merge(provisionLeaves).ToList().Select(_ => System.Reactive.Unit.Default);

        var existingSubtrees = partitions.Length == 0
            ? Observable.Return((IEnumerable<MeshNode>)Array.Empty<MeshNode>())
            : Observable.Zip(partitions.Select(p =>
                    meshService.Query<MeshNode>(
                            MeshQueryRequest.FromQuery($"path:{p} scope:descendants"))
                        .Take(1)
                        .Select(c => (IEnumerable<MeshNode>)c.Items)))
                .Select(lists => lists.SelectMany(x => x));

        return provision
            .SelectMany(_ => existingSubtrees)
            .Take(1)
            .SelectMany(existingItems =>
            {
                var existing = existingItems
                    .Where(n => !string.IsNullOrEmpty(n.Path))
                    .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // Subtrees a user has claimed wholesale — nothing at or under these paths is synced.
                var excludedRoots = existing.Values
                    .Where(n => n.SyncBehavior == SyncBehavior.ExcludeThisAndChildren)
                    .Select(n => n.Path)
                    .ToArray();

                bool IsClaimed(string path, MeshNode? target) =>
                    (target is not null && target.SyncBehavior != SyncBehavior.Include)
                    || excludedRoots.Any(root => IsAtOrUnder(path, root));

                // Per source node: skip claimed; create absent (the create pipeline materializes
                // content + prerender); overwrite existing as a Full (decoupled from merge-sync).
                var upserted = nodes
                    .Select(sourceNode =>
                    {
                        var path = sourceNode.Path;
                        existing.TryGetValue(path, out var target);
                        if (IsClaimed(path, target))
                        {
                            logger?.LogDebug(
                                "[StaticRepoImport] {Partition}: skip claimed {Path}", source.Partition, path);
                            return Observable.Return(0);
                        }

                        var materialized = Materialize(sourceNode);
                        if (target is null)
                            return CreateOne(meshService, materialized, logger);

                        // Preserve owner identity; Version is re-stamped by the owner on the Full.
                        var authoritative = materialized with
                        {
                            CreatedDate = target.CreatedDate,
                            CreatedBy = target.CreatedBy
                        };
                        return OverwriteOne(hub, authoritative);
                    })
                    .ToObservable()
                    .Merge(BatchSize)
                    .Sum();

                return upserted.SelectMany(count =>
                {
                    // Prune (full-replace): targets absent from source AND still Include AND not
                    // governance/satellite AND not under a claimed subtree. User-claimed nodes,
                    // _Policy/_Access governance, and the _Activity import history all survive.
                    var sourcePaths = nodes
                        .Select(n => n.Path)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var toPrune = existing.Values
                        .Where(t => !sourcePaths.Contains(t.Path)
                                    && t.SyncBehavior == SyncBehavior.Include
                                    && !IsGovernance(t)
                                    && !excludedRoots.Any(root => IsAtOrUnder(t.Path, root)))
                        .ToArray();

                    var pruned = toPrune.Length == 0
                        ? Observable.Return(0)
                        : toPrune
                            .Select(t => meshService.DeleteNode(t.Path)
                                .Select(_ => 1)
                                .Catch<int, Exception>(ex =>
                                {
                                    logger?.LogWarning(ex,
                                        "[StaticRepoImport] {Partition}: prune of {Path} failed (continuing).",
                                        source.Partition, t.Path);
                                    return Observable.Return(0);
                                }))
                            .ToObservable()
                            .Merge(BatchSize)
                            .Sum();

                    return pruned.SelectMany(prunedCount =>
                    {
                        NodeTypeCompilationActivity.AppendLog(hub, activityPath,
                            $"Imported {count} node(s), pruned {prunedCount}.", logger!);
                        NodeTypeCompilationActivity.MarkSucceeded(hub, activityPath, logger!);
                        logger?.LogInformation(
                            "[StaticRepoImport] {Partition}: imported {Count}, pruned {Pruned} at {Fingerprint}.",
                            source.Partition, count, prunedCount, fingerprint);
                        return Observable.Return(
                            new StaticRepoImportResult(source.Partition, fingerprint, "Imported", count));
                    });
                });
            })
            .Catch<StaticRepoImportResult, Exception>(ex =>
            {
                NodeTypeCompilationActivity.MarkFailed(hub, activityPath, ex.Message, logger!);
                logger?.LogWarning(ex, "[StaticRepoImport] {Partition} import failed.", source.Partition);
                return Observable.Return(new StaticRepoImportResult(source.Partition, fingerprint, "Failed"));
            });
    }

    /// <summary>Top-level partition segment of a path (the part before the first '/').</summary>
    private static string FirstSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>True if <paramref name="path"/> equals <paramref name="root"/> or is a descendant.</summary>
    private static bool IsAtOrUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Governance / lifecycle satellites — a <c>_</c>-prefixed segment AFTER the partition root
    /// (<c>X/_Policy</c>, <c>X/_Access/…</c>, <c>X/_Activity/…</c>). Never pruned: the in-memory
    /// provider serves the access policy/assignments and the import history lives under
    /// <c>_Activity</c>. A <c>_</c>-prefixed FIRST segment is a partition root (e.g. the
    /// <c>_Provider</c> model-provider partition), NOT governance — hence <c>Skip(1)</c>.
    /// </summary>
    private static bool IsGovernance(MeshNode node) =>
        node.Segments.Skip(1).Any(seg => seg.StartsWith('_'));

    /// <summary>
    /// Computes prerendered HTML for markdown nodes via the shared <see cref="MarkdownContent.Parse"/>
    /// (the exact call the runtime uses) so the materialized partition serves fully from the DB.
    /// Non-markdown content passes through unchanged.
    /// </summary>
    private static MeshNode Materialize(MeshNode node)
    {
        if (node.Content is MarkdownContent { Content.Length: > 0 } md)
        {
            var html = md.PrerenderedHtml ?? MarkdownContent.Parse(md.Content, node.Path, node.Path).PrerenderedHtml;
            return node with
            {
                State = MeshNodeState.Active,
                PreRenderedHtml = html,
                Content = md with { PrerenderedHtml = html }
            };
        }
        return node with { State = MeshNodeState.Active };
    }

    /// <summary>Create an absent node through the canonical create pipeline (content + prerender,
    /// already computed by <see cref="Materialize"/>). Returns 1 on success.</summary>
    private static IObservable<int> CreateOne(IMeshService meshService, MeshNode node, ILogger? logger) =>
        meshService.CreateNode(node)
            .FirstAsync()
            .Select(_ => 1)
            .Catch<int, Exception>(ex => Observable.Throw<int>(
                new InvalidOperationException($"Create of '{node.Path}' failed: {ex.Message}", ex)));

    /// <summary>Overwrite an existing node with the full authoritative state (ChangeType.Full),
    /// decoupled from the merge-sync protocol. Returns 1 on success.</summary>
    private static IObservable<int> OverwriteOne(IMessageHub hub, MeshNode node) =>
        hub.GetWorkspace().GetMeshNodeStream(node.Path).Overwrite(node)
            .FirstAsync()
            .Select(_ => 1)
            .Catch<int, Exception>(ex => Observable.Throw<int>(
                new InvalidOperationException($"Overwrite of '{node.Path}' failed: {ex.Message}", ex)));
}

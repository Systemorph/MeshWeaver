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
        // The partition root (namespace="", id=Partition) is a STANDARD part of every import — a
        // proper Space so the partition is routable + listable and has a landing page. Sources may
        // customize it (the Doc welcome page); otherwise we synthesize a generic Space root. It is
        // included in the fingerprint so editing the welcome re-imports.
        var root = ResolveRoot(source);
        var fingerprint = PartitionSourceFingerprint.Compute(
            nodes.Append(root).ToArray(), source.Versioned, hub.JsonSerializerOptions);
        var activityId = $"import-{fingerprint}";
        var activityNamespace = $"{source.Partition}/_Activity";
        var activityPath = $"{activityNamespace}/{activityId}";

        // 🚨 Provision the partition schema BEFORE the activity-lock create. The lock node lives at
        // {Partition}/_Activity/… — i.e. INSIDE the partition schema. On a not-yet-provisioned
        // partition the create would fault (42P01, no lazy schema create — see GhostSchemaInvariant)
        // and be misreported as "AlreadyRunning". EnsurePartitionProvisioned is reactive + pooled +
        // promise-cached, so Run's later re-provision of the touched partitions is a no-op.
        return ProvisionPartitions(hub, [source.Partition])
            // Short-circuit: a Succeeded import activity for this fingerprint = already imported.
            // (Existence check via query — eventually consistent, but the CreateNode lock below is the
            // authoritative guard; a stale miss just attempts the create and loses the race.)
            .SelectMany(_ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{activityPath}")))
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
                // Under System so the lock write authorizes on read-only-_Policy partitions and
                // persists on the distributed path (see AsSystem).
                return AsSystem(hub, () => meshService.CreateNode(activityNode))
                    .SelectMany(_ => Run(hub, source, nodes, root, activityPath, fingerprint, logger))
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
        MeshNode root, string activityPath, string fingerprint, ILogger? logger)
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
        // (source.Partition was already provisioned in Import before the activity lock; the promise
        // cache makes this a no-op for it and provisions any additional touched partition, e.g. _Provider.)
        var provision = ProvisionPartitions(hub, partitions);

        var existingSubtrees = partitions.Length == 0
            ? Observable.Return((IEnumerable<MeshNode>)Array.Empty<MeshNode>())
            : Observable.Zip(partitions.Select(p =>
                    meshService.Query<MeshNode>(
                            MeshQueryRequest.FromQuery($"path:{p} scope:descendants"))
                        .Take(1)
                        .Select(c => (IEnumerable<MeshNode>)c.Items)))
                .Select(lists => lists.SelectMany(x => x));

        return provision
            // Root-first: ensure the Space partition root exists (standard import step) before the
            // children. Children depend only on the schema (provisioned above), but the root is what
            // makes the partition a routable + listable Space with a landing page.
            .SelectMany(_ => EnsureRoot(hub, meshService, source, root, activityPath, logger))
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
                            return CreateOne(hub, meshService, materialized, logger);

                        // Preserve owner identity; Version is re-stamped on the fresh create.
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
                            .Select(t => AsSystem(hub, () => meshService.DeleteNode(t.Path))
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

    /// <summary>
    /// The partition root node the importer materializes (<c>namespace="", id={Partition}</c>). Uses
    /// the source's <see cref="IStaticRepoSource.PartitionRoot"/> customization when provided,
    /// otherwise synthesizes a generic <c>Space</c> root with a default welcome — so creating a
    /// proper Space is a STANDARD part of every import, never per-source opt-in.
    /// </summary>
    private static MeshNode ResolveRoot(IStaticRepoSource source) =>
        source.PartitionRoot ?? new MeshNode(source.Partition)
        {
            Name = source.Partition,
            NodeType = SpaceNodeTypeName,
            State = MeshNodeState.Active,
            Content = new MarkdownContent
            {
                Content = $"""
                    # {source.Partition}

                    Welcome. Explore the contents from the menu above, or use the chat input below to
                    ask a question or start a new thread.
                    """
            }
        };

    /// <summary>The <c>Space</c> node type — referenced by name to avoid a portal-assembly dependency.</summary>
    private const string SpaceNodeTypeName = "Space";

    /// <summary>
    /// Provisions each partition's storage (PG schema + satellite tables) via the standard
    /// <see cref="IPartitionStorageProvider.EnsurePartitionProvisioned"/> (reactive, pooled,
    /// promise-cached, lowercases the schema). Providers that don't own a partition no-op. Idempotent
    /// — safe to call for the same partition more than once across the import.
    /// </summary>
    private static IObservable<System.Reactive.Unit> ProvisionPartitions(
        IMessageHub hub, IEnumerable<string> partitions)
    {
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToArray();
        var leaves = partitions
            .Where(p => !string.IsNullOrEmpty(p))
            .SelectMany(p => providers.Select(pr => pr.EnsurePartitionProvisioned(p)))
            .ToArray();
        return leaves.Length == 0
            ? Observable.Return(System.Reactive.Unit.Default)
            : Observable.Merge(leaves).ToList().Select(_ => System.Reactive.Unit.Default);
    }

    /// <summary>
    /// Ensures the partition root exists as a proper <c>Space</c>. Read by EXACT path (not
    /// <c>scope:descendants</c>, which emits <c>LIKE 'P/%'</c> and never matches the
    /// <c>namespace=""</c> root). Absent → create through the canonical pipeline (a <c>Space</c>
    /// create triggers eager schema provisioning + the partition-definition/routing prime + the
    /// admin grant); present → overwrite to refresh the welcome, preserving owner identity. The
    /// create degrades to an overwrite on a concurrent-replica "already exists" so the import never
    /// faults on the lock race.
    /// </summary>
    private static IObservable<int> EnsureRoot(
        IMessageHub hub, IMeshService meshService, IStaticRepoSource source, MeshNode root,
        string activityPath, ILogger? logger) =>
        meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{source.Partition}"))
            .Take(1)
            .SelectMany(change =>
            {
                var existing = change.Items.FirstOrDefault(n => string.IsNullOrEmpty(n.Namespace));
                var materialized = Materialize(root);
                if (existing is null)
                {
                    NodeTypeCompilationActivity.AppendLog(
                        hub, activityPath, $"Creating Space root {source.Partition}…", logger!);
                    return CreateOne(hub, meshService, materialized, logger)
                        // Concurrent replica won the create between our read and write — replace
                        // instead of faulting the whole import (mirrors the activity-lock catch).
                        .Catch<int, Exception>(_ => OverwriteOne(hub, materialized));
                }

                var authoritative = materialized with
                {
                    CreatedDate = existing.CreatedDate,
                    CreatedBy = existing.CreatedBy
                };
                return OverwriteOne(hub, authoritative);
            });

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

    /// <summary>
    /// Establishes the well-known System identity ON THE WRITE'S OWN SUBSCRIBE THREAD. The single
    /// top-level <c>ImpersonateAsSystem</c> in <see cref="ImportAll"/> is NOT sufficient on the
    /// distributed/Orleans path: it sets an AsyncLocal on the (TaskPool) subscribe thread, but every
    /// cross-hub write is subscribed deep inside <c>.SelectMany</c>/<c>.Merge</c> lambdas that run on
    /// PG-query / remote-stream emission threads where that AsyncLocal is gone — so the write captures
    /// a null AccessContext, the owner's PostPipeline fails closed, and the write is silently dropped
    /// (while returning an optimistic snapshot). Re-establishing System synchronously around each
    /// write's subscribe makes <c>CreateNode</c>/<c>DeleteNode</c> capture System at their <c>Defer</c>,
    /// and makes <c>GetMeshNodeStream(...).Overwrite</c> capture System into its <c>capturedContext</c>
    /// (which the sync-stream post then carries). See Doc/Architecture/AccessContextPropagation.md.
    /// </summary>
    private static IObservable<T> AsSystem<T>(IMessageHub hub, Func<IObservable<T>> write)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access is null
            ? Observable.Defer(write)
            : Observable.Using(() => access.ImpersonateAsSystem(), _ => write());
    }

    /// <summary>Create an absent node through the canonical create pipeline (content + prerender,
    /// already computed by <see cref="Materialize"/>). Returns 1 on success. Idempotent: if the node
    /// already exists (the existing-subtree snapshot is eventually consistent and can miss a row, or
    /// a prior partial import created it), fall back to <see cref="OverwriteOne"/> rather than
    /// faulting the whole partition import.</summary>
    private static IObservable<int> CreateOne(IMessageHub hub, IMeshService meshService, MeshNode node, ILogger? logger) =>
        AsSystem(hub, () => meshService.CreateNode(node))
            .FirstAsync()
            .Select(_ => 1)
            .Catch<int, Exception>(ex =>
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                    ? OverwriteOne(hub, node)
                    : Observable.Throw<int>(
                        new InvalidOperationException($"Create of '{node.Path}' failed: {ex.Message}", ex)));

    /// <summary>Overwrite an existing node with the full authoritative state (ChangeType.Full).
    /// Non-destructive (no delete) — persistence depends on the owner→PG write-back carrying the
    /// caller's AccessContext (see <c>WriteViaSyncStream</c> + the owner-hub persistence path).</summary>
    private static IObservable<int> OverwriteOne(IMessageHub hub, MeshNode node) =>
        AsSystem(hub, () => hub.GetWorkspace().GetMeshNodeStream(node.Path).Overwrite(node))
            .FirstAsync()
            .Select(_ => 1)
            .Catch<int, Exception>(ex => Observable.Throw<int>(
                new InvalidOperationException($"Overwrite of '{node.Path}' failed: {ex.Message}", ex)));
}

using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
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
    /// Address-type prefix of the dedicated import hub (<c>import/{meshHubId}</c>). Declared
    /// stream-routed via <c>MeshBuilder.AddStreamRoutedAddressType(ImportAddressType)</c> in
    /// <c>AddGraph</c> so the silo's RoutingGrain dispatches to it over the cluster memory stream
    /// (and responses route back). Owned by this module — NOT hard-coded into the core
    /// <see cref="MeshConfiguration.DefaultStreamRoutedAddressTypes"/>. See <see cref="CreateImportHub"/>.
    /// </summary>
    public const string ImportAddressType = "import";

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

        // 🚨 The bulk create/upsert traffic of an import MUST NOT run on the ROOT MESH HUB's
        // action block. The mesh hub is the irreplaceable router; flooding it with
        // CreateOrUpdateNodeRequest (each fanning out an inner self-posted CreateNodeRequest)
        // stalls ALL routing → every node op 60s-times-out → portal-wide wedge (atioz 2026-06-11:
        // 11× CreateOrUpdateNodeRequest + 3× CreateNodeRequest@mesh/<self> stale >60s while real
        // user SubscribeRequests starved). Run the whole import on a DEDICATED reachable hub
        // instead — see CreateImportHub. Its own single-threaded action block serialises the
        // import; the mesh hub stays free to route.
        var importHub = CreateImportHub(hub, logger);

        // Establish System identity for the whole import subscription so each source's writes
        // capture it (CarryAccessContext) — disposed when the import completes.
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
            // 🚨 Per-SOURCE isolation: one source faulting must NOT abort the others. Each
            // Import is guarded; a failure logs + yields a "Failed" result and the Concat
            // continues to the next source. (Per-FILE isolation lives one level deeper, in
            // Run's upsert loop, so a single bad node never fails its whole partition.)
            _ => sources
                .Select(s => Import(importHub, s, logger)
                    .Catch<StaticRepoImportResult, Exception>(ex =>
                    {
                        logger?.LogWarning(ex, "[StaticRepoImport] source {Partition} failed.", s.Partition);
                        return Observable.Return(new StaticRepoImportResult(s.Partition, string.Empty, "Failed"));
                    }))
                .Concat());
    }

    /// <summary>
    /// Imports a SINGLE caller-supplied source on the dedicated import hub (off the router) — the
    /// runtime entry point for ad-hoc imports such as "GitHub → new Space". Mirrors
    /// <see cref="ImportAll"/>'s hub handling (create/reuse <c>import/{meshId}</c>) for one source.
    /// Per-write System impersonation lives inside <see cref="Import"/> (its <see cref="AsSystem"/>
    /// wrapper), so callers pre-create any user-owned partition root under the user first, then call
    /// this for the content. Reactive — Subscribe to run.
    /// </summary>
    public static IObservable<StaticRepoImportResult> ImportSource(
        IMessageHub meshHub, IStaticRepoSource source, ILogger? logger = null)
    {
        logger ??= meshHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var importHub = CreateImportHub(meshHub, logger);
        return Import(importHub, source, logger);
    }

    /// <summary>
    /// Creates (or returns the existing) DEDICATED import hub — the reachable hosted hub the whole
    /// static-repo import runs on, so its bulk <see cref="CreateOrUpdateNodeRequest"/> /
    /// <see cref="CreateNodeRequest"/> traffic is processed on THIS hub's action block, never the
    /// root mesh hub's (the router must stay free — see <see cref="ImportAll"/>).
    ///
    /// <para>Same reachable-hosted-hub pattern as <c>MeshNodeStreamCache</c>'s cache hub:</para>
    /// <list type="bullet">
    ///   <item>A PROCESS-UNIQUE address <c>import/{meshHubId}</c> — the <c>import</c> address-type is
    ///     declared stream-routed in <see cref="MeshConfiguration.DefaultStreamRoutedAddressTypes"/>,
    ///     so the silo's RoutingGrain dispatches to it via the cluster memory stream (keyed by the
    ///     mesh hub's Id so each process gets its own subscription).</item>
    ///   <item><c>WithInitialization(... RegisterStream(hub))</c> — registers the hub with the routing
    ///     service BEFORE any request is posted, so responses (query results, ImportContent acks)
    ///     route back to it instead of falling into the mesh-type NotFound trap.</item>
    ///   <item><c>WithNodeOperationHandlers()</c> — the node create/upsert handlers, so the import's
    ///     posts are handled locally on this hub (and the handler's inner self-posted
    ///     <c>CreateNodeRequest</c> stays on this hub too, never the mesh hub).</item>
    ///   <item><c>AddData()</c> — an <see cref="IWorkspace"/> so the upsert-of-existing path
    ///     (<c>hub.GetMeshNodeStream(path).Update</c>) can dispatch through the shared
    ///     <c>IMeshNodeStreamCache</c>.</item>
    /// </list>
    /// <see cref="HostedHubCreation.Always"/> + the stable address makes this idempotent: repeated
    /// <see cref="ImportAll"/> calls (and direct test <see cref="Import"/> calls that pass the mesh
    /// hub) reuse the one hub.
    /// </summary>
    private static IMessageHub CreateImportHub(IMessageHub meshHub, ILogger? logger)
    {
        var routingService = meshHub.ServiceProvider.GetRequiredService<IRoutingService>();
        var importAddress = new Address(ImportAddressType, meshHub.Address.Id);
        logger?.LogInformation("[StaticRepoImport] dedicated import hub at {Address} (off the mesh router).", importAddress);
        return meshHub.GetHostedHub(
            importAddress,
            config => config
                .AddData()
                .WithNodeOperationHandlers()
                .WithInitialization(h => h.RegisterForDisposal(routingService.RegisterStream(h))),
            HostedHubCreation.Always)!;
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
                var existing = change.Items.FirstOrDefault();
                if (existing?.Content is ActivityLog { Status: ActivityStatus.Succeeded })
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

                // 🚨 A NON-Succeeded existing activity is a STALE lock — a prior import created it
                // (Status=Running) then crashed/raced before finishing (e.g. a rollout briefly running
                // two pods, or a materialization fault). Skipping it forever ("AlreadyRunning", 0
                // nodes) wedges the partition's import permanently — the atioz Agent/Harness/Command
                // never materialized for exactly this reason. RECLAIM it: delete the dead lock so the
                // CreateNode below re-acquires and re-runs. Absent → the delete is a harmless no-op.
                // (A genuinely-concurrent replica re-runs the idempotent Upsert materialization — wasteful
                // but correct; this is far better than a permanent wedge on a single-replica deploy.)
                var clearStale = existing is null
                    ? Observable.Return(System.Reactive.Unit.Default)
                    : AsSystem(hub, () => meshService.DeleteNode(activityPath))
                        .Select(_ => System.Reactive.Unit.Default)
                        .Catch(Observable.Return(System.Reactive.Unit.Default));

                // CreateNode is the lock: first instance wins; concurrent replicas fault here.
                // Under System so the lock write authorizes on read-only-_Policy partitions and
                // persists on the distributed path (see AsSystem).
                return clearStale.SelectMany(_ => AsSystem(hub, () => meshService.CreateNode(activityNode))
                    .SelectMany(_ => Run(hub, source, nodes, root, activityPath, fingerprint, logger))
                    .Catch<StaticRepoImportResult, Exception>(ex =>
                    {
                        logger?.LogInformation(
                            "[StaticRepoImport] {Partition} ({Fingerprint}) lock held / create faulted: {Message}",
                            source.Partition, fingerprint, ex.Message);
                        return Observable.Return(
                            new StaticRepoImportResult(source.Partition, fingerprint, "AlreadyRunning"));
                    }));
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
            .SelectMany(_ => EnsureRoot(hub, source, root, activityPath, logger))
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

                // Per source node: skip claimed; otherwise UPSERT through the single canonical verb
                // (CreateOrUpdateNodeRequest — the same path NodeCopyHelper uses). It creates absent
                // nodes and updates existing ones (version re-stamped by the owner), materializing
                // content + prerender through the real pipeline. Preserve owner identity on update.
                var upserted = nodes
                    .Select(sourceNode =>
                    {
                        var path = sourceNode.Path;
                        existing.TryGetValue(path, out var target);
                        if (IsClaimed(path, target))
                        {
                            logger?.LogDebug(
                                "[StaticRepoImport] {Partition}: skip claimed {Path}", source.Partition, path);
                            return Observable.Return((Imported: 0, Failed: 0));
                        }

                        var materialized = Materialize(sourceNode);
                        if (target is not null)
                            materialized = materialized with
                            {
                                CreatedDate = target.CreatedDate,
                                CreatedBy = target.CreatedBy
                            };
                        // 🚨 Per-FILE isolation. A single node's upsert faulting (bad content, a
                        // validator reject, a transient owner timeout) must NOT abort the whole
                        // partition import — the first failure used to propagate through Merge and
                        // kill every remaining node. Guard each create: log the exact path to the
                        // logger AND append a per-file ⚠ line to the import activity (so the failure
                        // is diagnosable from the activity log after the fact), then count it as a
                        // Failed and continue. The Failed tally drives the terminal Warning status
                        // below — the activity never reports a green Succeeded while hiding failures.
                        return Upsert(hub, materialized)
                            .Select(_ => (Imported: 1, Failed: 0))
                            .Catch<(int Imported, int Failed), Exception>(ex =>
                            {
                                logger?.LogWarning(ex,
                                    "[StaticRepoImport] {Partition}: upsert of {Path} failed (continuing).",
                                    source.Partition, path);
                                NodeTypeCompilationActivity.AppendLog(hub, activityPath,
                                    $"⚠ Failed to import {path}: {ex.Message}", logger!,
                                    Microsoft.Extensions.Logging.LogLevel.Warning);
                                return Observable.Return((Imported: 0, Failed: 1));
                            });
                    })
                    .ToObservable()
                    .Merge(BatchSize)
                    .Aggregate((Imported: 0, Failed: 0),
                        (acc, x) => (acc.Imported + x.Imported, acc.Failed + x.Failed));

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
                        // Sync content-collection files (the assets behind @@content/<file> embeds)
                        // collection→collection into each owning node — AFTER the node upsert.
                        SyncContentImports(hub, source, logger).Select(contentCount =>
                        {
                            // 🚨 Terminal status reflects per-file outcomes: ANY failed upsert →
                            // Warning (the ⚠ lines above pinpoint which files), all-clear →
                            // Succeeded. Written via Complete so the status + summary land in ONE
                            // atomic Update — a reader observing the terminal status always sees the
                            // full diagnostic log (no torn "Succeeded but the ⚠ lines didn't land yet").
                            var failed = count.Failed;
                            var status = failed > 0 ? ActivityStatus.Warning : ActivityStatus.Succeeded;
                            var summary = failed > 0
                                ? $"Imported {count.Imported} node(s), {failed} FAILED (see ⚠ above), pruned {prunedCount}, synced {contentCount} content file(s)."
                                : $"Imported {count.Imported} node(s), pruned {prunedCount}, synced {contentCount} content file(s).";
                            NodeTypeCompilationActivity.Complete(hub, activityPath, status,
                                new[]
                                {
                                    new LogMessage(summary,
                                        failed > 0
                                            ? Microsoft.Extensions.Logging.LogLevel.Warning
                                            : Microsoft.Extensions.Logging.LogLevel.Information)
                                },
                                logger!);
                            logger?.LogInformation(
                                "[StaticRepoImport] {Partition}: imported {Count}, failed {Failed}, pruned {Pruned}, content {Content} at {Fingerprint}.",
                                source.Partition, count.Imported, failed, prunedCount, contentCount, fingerprint);
                            return new StaticRepoImportResult(source.Partition, fingerprint,
                                failed > 0 ? "ImportedWithErrors" : "Imported", count.Imported);
                        }));
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
        IMessageHub hub, IStaticRepoSource source, MeshNode root,
        string activityPath, ILogger? logger)
    {
        NodeTypeCompilationActivity.AppendLog(
            hub, activityPath, $"Ensuring Space root {source.Partition}…", logger!);
        // Upsert through the canonical verb — creating a Space triggers eager schema provisioning +
        // the partition-definition/routing prime + the admin grant; an existing root is updated
        // (owner preserves identity). Same path as every other node.
        return Upsert(hub, Materialize(root));
    }

    /// <summary>
    /// Syncs the source's content-collection imports (<see cref="IStaticRepoSource.EnumerateContentImports"/>)
    /// — copies each declared source-collection folder into the owning node's content collection via the
    /// canonical <see cref="ContentImportExtensions.ImportContent"/> operation, posted to the node's hub
    /// under System (never a hand-rolled cross-hub write). Per-entry failures log and continue. Returns
    /// the total files imported.
    /// </summary>
    private static IObservable<int> SyncContentImports(IMessageHub hub, IStaticRepoSource source, ILogger? logger)
    {
        var imports = source.EnumerateContentImports();
        if (imports.Count == 0)
            return Observable.Return(0);

        return imports
            .Select(import => AsSystem(hub, () => hub.ImportContent(import.NodePath)
                    .From(import.SourceCollection, import.SourcePath)
                    .To(import.TargetCollection, import.TargetPath)
                    .Post())
                .Select(r => r.Success ? r.FilesImported : 0)
                .Catch<int, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[StaticRepoImport] {Partition}: content import for {Node} failed (continuing).",
                        source.Partition, import.NodePath);
                    return Observable.Return(0);
                }))
            .ToObservable()
            .Merge(BatchSize)
            .Sum();
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

    /// <summary>
    /// Upsert a node through the SINGLE canonical verb <see cref="CreateOrUpdateNodeRequest"/> — the
    /// same path <c>NodeCopyHelper</c> uses. The handler creates the node when absent and updates it
    /// when present (the owner re-stamps Version), running the full pipeline (prerender, embedding,
    /// satellite + access). This is the documented step 4 of the static-repo import (see
    /// StaticRepoImport.md) — NOT a hand-rolled CreateNode/stream-Overwrite split. Returns 1.
    /// </summary>
    private static IObservable<int> Upsert(IMessageHub hub, MeshNode node) =>
        AsSystem(hub, () => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)))
            .FirstAsync()
            .Select(d => d.Message)
            .SelectMany(resp => resp.Success
                ? Observable.Return(1)
                : Observable.Throw<int>(new InvalidOperationException(
                    $"Upsert of '{node.Path}' failed: {resp.Error}")));
}

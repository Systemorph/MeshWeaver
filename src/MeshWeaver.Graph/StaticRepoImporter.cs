using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
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

/// <summary>Outcome of a <see cref="StaticRepoImporter.Import"/> run. <paramref name="Preserved"/> is
/// the number of live nodes a two-way import kept because they were newer on the server (see
/// <see cref="ImportConflictPolicy"/>) — non-zero means the mesh is now AHEAD of the repo, so the
/// caller must NOT advance its last-sync baseline until those edits are committed back.</summary>
public sealed record StaticRepoImportResult(string Partition, string Fingerprint, string Outcome, int Count = 0, int Preserved = 0);

/// <summary>
/// Conflict policy for an import that reconciles against a LIVE partition — the GitHub
/// "Update to latest" path. Git-first by default (<see cref="GitFirst"/>): the repo overwrites the
/// live node and prunes extras. Two-way (<see cref="PreserveServerNewer"/>) instead PRESERVES a node
/// that was changed on the server since the last sync, so a local edit made between syncs is carried
/// back to the repo on the next commit rather than silently overwritten — "newer on the server wins".
/// A <see cref="Force"/> import ignores the policy and overwrites regardless (the deliberate-discard
/// escape hatch). Boot / built-in static-repo imports pass no policy → <see cref="GitFirst"/>.
/// </summary>
public sealed record ImportConflictPolicy(bool PreserveServerNewer, DateTimeOffset? Since, bool Force = false)
{
    /// <summary>The git-first default: the repo is authoritative; local edits are overwritten/pruned.</summary>
    public static readonly ImportConflictPolicy GitFirst = new(false, null, false);

    /// <summary>
    /// True when <paramref name="target"/> is a live node changed on the SERVER since the last sync
    /// (<see cref="Since"/>) and must therefore be PRESERVED (not overwritten, not pruned) — unless
    /// this is a <see cref="Force"/> import. Requires a recorded <see cref="Since"/>: with no sync
    /// baseline there is nothing to protect, so a first import stays git-first. Pure — unit-testable.
    /// </summary>
    public bool PreservesServerCopyOf(MeshNode? target) =>
        PreserveServerNewer && !Force && Since is { } since
        && target is not null && target.LastModified > since;
}

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
    /// <summary>
    /// Max CONCURRENT upserts/prunes per source (the <c>.Merge(BatchSize)</c> bound). Each
    /// <see cref="CreateOrUpdateNodeRequest"/> is heavyweight — it fans out an inner create + prerender
    /// + embedding + satellite/access — so a wide fan-out spikes CPU and pressures the import hub's
    /// action block on boot. Kept deliberately SMALL (≈5 in flight) so the boot import trickles rather
    /// than floods; sources still run sequentially (<c>.Concat()</c>), so this is the only concurrency.
    /// </summary>
    private const int BatchSize = 5;

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
    /// <c>RlsNodeValidator</c> short-circuits to Valid for the well-known System identity
    /// (it bypasses RLS entirely — it does NOT rely on a <see cref="MeshWeaver.Mesh.Security.Permission.Sync"/>
    /// grant, which the read-only <c>_Policy</c> cap would strip anyway). System-based sync is the
    /// intended mechanism for built-in static-repo content.
    /// Sources are imported sequentially (bounded boot load). Reactive — Subscribe to run.
    /// </summary>
    public static IObservable<StaticRepoImportResult> ImportAll(
        IMessageHub hub, ILogger? logger = null,
        IReadOnlyDictionary<string, PartitionSyncMode>? syncModeOverrides = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var sources = hub.ServiceProvider.GetServices<IStaticRepoSource>().ToArray();
        if (sources.Length == 0)
            return Observable.Empty<StaticRepoImportResult>();

        // Deploy-time per-partition mode override (Features:StaticRepoSync:Modes) — case-insensitive.
        // When a partition isn't listed the source's own default SyncMode is used (FullReplace for most,
        // Additive for the built-in AI catalogs). Null/empty = every source keeps its own default.
        var modeOverrides = syncModeOverrides is { Count: > 0 }
            ? new Dictionary<string, PartitionSyncMode>(syncModeOverrides, StringComparer.OrdinalIgnoreCase)
            : null;

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

        // The partitions the COMPILED static-repo sources own this run — the only set eligible for
        // source-owned pruning below (GitSync's ImportSource is NOT in this list and writes no marker).
        var currentSourcePartitions = sources
            .Select(s => s.Partition)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Establish System identity for the whole import subscription so each source's writes
        // capture it (CarryAccessContext) — disposed when the import completes.
        return Observable.Using(
            () => accessService?.ImpersonateAsSystem() ?? Disposable.Empty,
            // 🚨 Per-SOURCE isolation: one source faulting must NOT abort the others. Each
            // Import is guarded; a failure logs + yields a "Failed" result and the Concat
            // continues to the next source. (Per-FILE isolation lives one level deeper, in
            // Run's upsert loop, so a single bad node never fails its whole partition.)
            _ => sources
                .Select(s => Import(importHub, s, logger,
                        modeOverrides is not null && modeOverrides.TryGetValue(s.Partition, out var m)
                            ? m : null)
                    .Catch<StaticRepoImportResult, Exception>(ex =>
                    {
                        logger?.LogWarning(ex, "[StaticRepoImport] source {Partition} failed.", s.Partition);
                        return Observable.Return(new StaticRepoImportResult(s.Partition, string.Empty, "Failed"));
                    }))
                .Concat()
                // 🚨 After every source has imported, RECONCILE the source-owned catalog set: prune any
                // partition a compiled source USED to own but no longer does (e.g. the retired `command`
                // catalog after commands were unified into Skill). Source-owned ONLY — tracked via markers
                // this method writes, which GitSync/user partitions never get — so user data is never
                // touched. Guarded so a prune failure can't fail the whole import.
                .Concat(Observable.Defer(() =>
                    ReconcileSourceOwnedPartitions(importHub, currentSourcePartitions, logger)
                        .Catch<StaticRepoImportResult, Exception>(ex =>
                        {
                            logger?.LogWarning(ex, "[StaticRepoImport] source-owned partition reconcile failed.");
                            return Observable.Return(new StaticRepoImportResult("_SourceOwnedCatalogs", string.Empty, "Failed"));
                        }))));
    }

    /// <summary>Namespace under the Admin partition where one marker node per source-owned catalog
    /// partition is kept (id == partition name). The marker is the durable record that a partition was
    /// imported by a COMPILED <see cref="IStaticRepoSource"/> — and therefore the ONLY thing the
    /// reconcile below may prune. GitSync (<see cref="ImportSource"/>) writes no marker.</summary>
    internal const string SourceOwnedRegistryNamespace = "Admin/_SourceOwnedCatalogs";

    /// <summary>
    /// Orphaned source-owned partitions: those <paramref name="previouslyOwned"/> (recorded by a prior
    /// run's markers) that are no longer backed by a registered source (<paramref name="currentSources"/>).
    /// Pure + case-insensitive so it is unit-testable without a database — the "removing partitions"
    /// decision. A partition that is still a current source, or was never source-owned, is never returned.
    /// </summary>
    public static IReadOnlyList<string> ComputeOrphanedSourcePartitions(
        IEnumerable<string> previouslyOwned, IEnumerable<string> currentSources)
    {
        var current = new HashSet<string>(
            currentSources.Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
        return previouslyOwned
            .Where(p => !string.IsNullOrEmpty(p) && !current.Contains(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The nodes an import should PRUNE for a partition running <paramref name="mode"/>: existing
    /// partition nodes absent from the current source (<paramref name="sourcePaths"/>), still
    /// <see cref="SyncBehavior.Include"/>, not governance (<c>_Policy</c>/<c>_Access</c>/<c>_Activity</c>),
    /// and not at/under a claimed/excluded root (<paramref name="excludedRoots"/>). The mode narrows the
    /// candidate set:
    /// <list type="bullet">
    ///   <item><see cref="PartitionSyncMode.FullReplace"/> — every such extra (mirror the partition to the repo).</item>
    ///   <item><see cref="PartitionSyncMode.Additive"/> — ONLY extras the source PREVIOUSLY owned
    ///     (<paramref name="previouslyOwnedPaths"/> = the prior manifest's keys); a user-added node
    ///     that was never in any manifest is kept.</item>
    ///   <item><see cref="PartitionSyncMode.UpsertOnly"/> — none (never prune).</item>
    /// </list>
    /// Pure + case-insensitive so the prune DECISION is unit-testable without a database. This is the
    /// per-partition policy; the per-node <see cref="SyncBehavior"/> guard above applies in every mode
    /// (a claimed node is never a candidate).
    /// </summary>
    public static IReadOnlyList<MeshNode> ComputePrunableNodes(
        IEnumerable<MeshNode> existing,
        IEnumerable<string> sourcePaths,
        IEnumerable<string> previouslyOwnedPaths,
        IEnumerable<string> excludedRoots,
        PartitionSyncMode mode)
    {
        if (mode == PartitionSyncMode.UpsertOnly)
            return Array.Empty<MeshNode>();

        var source = new HashSet<string>(
            sourcePaths.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
        var previouslyOwned = new HashSet<string>(
            previouslyOwnedPaths.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
        var excluded = excludedRoots.ToArray();

        return existing
            .Where(t => !string.IsNullOrEmpty(t.Path)
                        && !source.Contains(t.Path)
                        && t.SyncBehavior == SyncBehavior.Include
                        && !IsGovernance(t)
                        && !excluded.Any(root => IsAtOrUnder(t.Path, root))
                        // Additive: only prune what the source PREVIOUSLY owned — a user-added node
                        // (never in a manifest) survives. FullReplace prunes every extra.
                        && (mode != PartitionSyncMode.Additive || previouslyOwned.Contains(t.Path)))
            .ToArray();
    }

    /// <summary>
    /// Reconciles the source-owned catalog set: (1) reads the existing markers under
    /// <see cref="SourceOwnedRegistryNamespace"/>, (2) DELETES every partition that is orphaned
    /// (<see cref="ComputeOrphanedSourcePartitions"/>) — its subtree AND its marker — and (3) writes a
    /// marker for each current source so the next run knows what it owns. Runs on the dedicated import
    /// hub under System (so cross-partition deletes authorise). Reactive end-to-end.
    /// </summary>
    private static IObservable<StaticRepoImportResult> ReconcileSourceOwnedPartitions(
        IMessageHub hub, IReadOnlyCollection<string> currentSourcePartitions, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

        return ProvisionPartitions(hub, ["Admin"])
            .SelectMany(_ => meshService.Query<MeshNode>(
                    MeshQueryRequest.FromQuery($"namespace:{SourceOwnedRegistryNamespace} scope:children"))
                .Take(1))
            .SelectMany(change =>
            {
                var previouslyOwned = change.Items
                    .Select(n => n.Id)               // marker id == partition name
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToArray();
                var orphans = ComputeOrphanedSourcePartitions(previouslyOwned, currentSourcePartitions);

                // Delete each orphan partition (recursive subtree) + its marker — System-scoped, guarded
                // per-orphan so one failure doesn't abort the rest.
                var prunes = orphans.Select(orphan =>
                {
                    logger?.LogInformation("[StaticRepoImport] pruning orphaned source-owned partition '{Partition}'.", orphan);
                    return AsSystem(hub, () => meshService.DeleteNode(orphan)).Select(_ => 1)
                        .Concat(AsSystem(hub, () => meshService.DeleteNode($"{SourceOwnedRegistryNamespace}/{orphan}")).Select(_ => 0))
                        .Catch<int, Exception>(ex =>
                        {
                            logger?.LogWarning(ex, "[StaticRepoImport] prune of '{Partition}' failed (continuing).", orphan);
                            return Observable.Return(0);
                        });
                });

                // Ensure a marker exists for every current source partition (idempotent upsert).
                // 🚨 The marker is a NESTED bookkeeping node under Admin/_SourceOwnedCatalogs/{p}, NOT a
                // partition root. It MUST NOT be a partition-owning NodeType (Space): a node whose type
                // has OwnsPartition=true is a partition root and OwnsPartitionProvisioningValidator
                // rejects it under any namespace ("A 'Space' owns its partition, so it must be top-level"
                // — InvalidPath). With NodeType=Space every marker write was rejected (Agent, Skill,
                // Provider, Doc, Harness), so the reconcile could never persist its ownership markers →
                // previouslyOwned read back EMPTY every run. Use the plain Markdown content type (matches
                // the MarkdownContent below, owns no partition) so the nested marker is valid.
                var markers = currentSourcePartitions.Select(p =>
                    Upsert(hub, new MeshNode(p, SourceOwnedRegistryNamespace)
                    {
                        NodeType = MarkdownNodeTypeName,
                        Name = p,
                        State = MeshNodeState.Active,
                        Content = new MarkdownContent { Content = $"Source-owned catalog marker for `{p}`." }
                    }).Catch<int, Exception>(_ => Observable.Return(0)));

                var work = prunes.Concat(markers).ToList();
                var prunedCount = orphans.Count;
                return (work.Count == 0 ? Observable.Return(0) : work.ToObservable().Merge(BatchSize).Sum())
                    .Select(_ =>
                    {
                        logger?.LogInformation(
                            "[StaticRepoImport] source-owned reconcile: {Pruned} pruned, {Owned} owned.",
                            prunedCount, currentSourcePartitions.Count);
                        return new StaticRepoImportResult("_SourceOwnedCatalogs", string.Empty,
                            prunedCount > 0 ? "Pruned" : "Reconciled", prunedCount);
                    });
            });
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
        IMessageHub meshHub, IStaticRepoSource source, ILogger? logger = null,
        ImportConflictPolicy? policy = null)
    {
        logger ??= meshHub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var importHub = CreateImportHub(meshHub, logger);
        return Import(importHub, source, logger, policy: policy);
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
                // 🚨 The import is INFRASTRUCTURE that runs on THIS hub's own action block — a
                // different thread from the caller's ImpersonateAsSystem scope, so that AsyncLocal
                // does NOT reach here. Declare the hub System so its own node/partition/activity
                // writes carry the system-security identity instead of failing closed under the
                // never-null guard (the import-activity phantom + NotFound storm, atioz 2026-06-18).
                .WithPostingIdentity(PostingIdentity.System)
                .AddData()
                .WithNodeOperationHandlers()
                .WithInitialization(h => h.RegisterForDisposal(routingService.RegisterStream(h))),
            HostedHubCreation.Always)!;
    }

    /// <summary>
    /// Imports a single static-repo source into its partition: provisions the partition
    /// schema, acquires the content-addressed import activity lock (idempotent — a prior
    /// Succeeded activity for the same fingerprint short-circuits), then upserts every
    /// source node, prunes stale ones, and syncs content imports. Runs on the supplied
    /// (dedicated import) hub. Reactive — Subscribe to run.
    /// </summary>
    /// <param name="hub">The hub the import runs on (typically the dedicated import hub).</param>
    /// <param name="source">The static-repo source to materialize.</param>
    /// <param name="logger">Optional logger; resolved from the hub when null.</param>
    /// <param name="syncModeOverride">Optional deploy-time override of the partition's
    /// <see cref="PartitionSyncMode"/>; when null the source's own <see cref="IStaticRepoSource.SyncMode"/>
    /// is used.</param>
    /// <returns>An observable that emits the import outcome for the partition.</returns>
    public static IObservable<StaticRepoImportResult> Import(
        IMessageHub hub, IStaticRepoSource source, ILogger? logger = null,
        PartitionSyncMode? syncModeOverride = null, ImportConflictPolicy? policy = null)
    {
        logger ??= hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.StaticRepoImporter");
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        // Effective per-partition prune policy: the deploy-time override wins, else the source's default.
        var syncMode = syncModeOverride ?? source.SyncMode;

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

                // The content-skip path: the content fingerprint matches a prior Succeeded import, so
                // re-ensure ONLY the CRITICAL governance (partition ROOT + its read-only _Policy —
                // Harness/Agent/Skill/Provider PublicRead). These MUST exist on every boot: if dropped
                // after a prior import (a cross-partition prune, a manual delete, a botched migration)
                // the content-skip would otherwise leave the partition UNREADABLE FOREVER (the "user
                // 'rbuergi' lacks Read permission on 'harness'" composer denial). EnsureRoot + the
                // _Policy upsert are idempotent; best-effort so a self-heal hiccup never fails the import.
                IObservable<StaticRepoImportResult> SkipWithGovernanceHeal()
                {
                    var governance = nodes
                        .Where(n => n.NodeType == "PartitionAccessPolicy"
                                    || n.Segments.Skip(1).Any(seg => seg.StartsWith('_')))
                        .ToArray();
                    logger?.LogInformation(
                        "[StaticRepoImport] {Partition} already at {Fingerprint} — content skipped; re-ensuring root + {Count} governance node(s).",
                        source.Partition, fingerprint, governance.Length);
                    return EnsureRoot(hub, source, root, activityPath, logger)
                        .SelectMany(_ => governance.Length == 0
                            ? Observable.Return(0)
                            : governance
                                .Select(g => Upsert(hub, Materialize(g))
                                    .Catch<int, Exception>(_ => Observable.Return(0)))
                                .ToObservable().Merge(BatchSize).Sum())
                        .Select(_ => new StaticRepoImportResult(source.Partition, fingerprint, "Skipped"))
                        .Catch<StaticRepoImportResult, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[StaticRepoImport] {Partition}: governance self-heal failed (continuing skipped).",
                                source.Partition);
                            return Observable.Return(new StaticRepoImportResult(source.Partition, fingerprint, "Skipped"));
                        });
                }

                // The full (re-)import path. Acquire/refresh the import lock via the IDEMPOTENT Upsert
                // (which tolerates a stale "already exists"). This RECLAIMS a dead lock left by a
                // crashed/raced prior import (Status=Running that never finished — e.g. a rollout
                // briefly running two pods, or a materialization fault) instead of skipping forever
                // ("AlreadyRunning", 0 nodes): the atioz Agent/Harness/Command wedge. Under System
                // (Upsert wraps AsSystem) so the lock write authorizes on read-only-_Policy partitions.
                IObservable<StaticRepoImportResult> Reimport()
                {
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
                    return Upsert(hub, activityNode)
                        .SelectMany(_ => Run(hub, source, nodes, root, activityPath, fingerprint, syncMode, logger, policy))
                        .Catch<StaticRepoImportResult, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "[StaticRepoImport] {Partition} ({Fingerprint}) import failed: {Message}",
                                source.Partition, fingerprint, ex.Message);
                            // Surface to the activity log + a GUI bell notification linking to it —
                            // a failed boot import must be SEEN, never a silent wedge.
                            NodeTypeCompilationActivity.MarkFailed(hub, activityPath, ex.Message, logger!);
                            NotifyStartupFailure(hub, source.Partition, activityPath, ex.Message, logger);
                            return Observable.Return(
                                new StaticRepoImportResult(source.Partition, fingerprint, "Failed"));
                        });
                }

                // No Succeeded marker → import. 🚨 Read the marker through the TOLERANT ContentAs, NEVER
                // a raw `existing.Content is ActivityLog {…}` pattern: a node read back from storage/query
                // carries its Content as a JsonElement, not the typed record, so the pattern-match fails
                // on a genuine Succeeded marker and re-imports every single time. ContentAs recovers the
                // degraded JsonElement (typed → as-is, JsonElement → deserialized, else null + logged).
                var existingLog = existing?.ContentAs<ActivityLog>(hub.JsonSerializerOptions, logger);
                if (existingLog is not { Status: ActivityStatus.Succeeded })
                    return Reimport();

                // A FORCED import must re-apply even when the content fingerprint is unchanged: its
                // purpose is to overwrite/prune local edits back to the (possibly identical) repo
                // state, which the content-skip short-circuit below would otherwise bypass — leaving
                // the very local changes the caller asked to discard. Force always re-runs.
                if (policy?.Force == true)
                {
                    logger?.LogInformation(
                        "[StaticRepoImport] {Partition}: forced re-import at unchanged fingerprint {Fingerprint}.",
                        source.Partition, fingerprint);
                    return Reimport();
                }

                // 🚨 SELF-HEAL CONTENT, not just governance. A Succeeded marker means "imported once" —
                // but the content NODES can be dropped after the fact (a cross-partition prune, a manual
                // delete, a botched migration) while the marker + _Policy survive, leaving the partition's
                // skills MISSING FOREVER ("a user didn't see the core app skills", atioz). Cheaply verify
                // a CONTENT sentinel is still present; if it's gone, fall through to the full idempotent
                // re-import instead of skipping. Eventually-consistent query: a stale miss re-imports
                // idempotently (wasteful, not wrong; the activity lock still serialises concurrent runs).
                var contentSentinel = nodes.FirstOrDefault(n =>
                    n.NodeType != "PartitionAccessPolicy"
                    && !n.Segments.Skip(1).Any(seg => seg.StartsWith('_')));
                if (contentSentinel is null)
                    return SkipWithGovernanceHeal(); // governance-only source — no content to verify

                return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{contentSentinel.Path}"))
                    .Take(1)
                    .SelectMany(sentinelChange =>
                    {
                        if (sentinelChange.Items.Any())
                            return SkipWithGovernanceHeal();
                        logger?.LogWarning(
                            "[StaticRepoImport] {Partition}: marker at {Fingerprint} says imported, but content sentinel '{Path}' is MISSING — self-healing via full re-import.",
                            source.Partition, fingerprint, contentSentinel.Path);
                        return Reimport();
                    });
            });
    }

    private static IObservable<StaticRepoImportResult> Run(
        IMessageHub hub, IStaticRepoSource source, IReadOnlyList<MeshNode> nodes,
        MeshNode root, string activityPath, string fingerprint, PartitionSyncMode syncMode, ILogger? logger,
        ImportConflictPolicy? policy = null)
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
            // Authoritatively read each partition ROOT's claim (GetMeshNodeStream, NOT the lagged query
            // snapshot above): a just-set "sync: none" (ExcludeThisAndChildren) must be honoured even
            // before the eventually-consistent read-model catches up, or the next import silently
            // re-enables sync on the freshly-decoupled partition. CQRS: never decide on a single node's
            // content from the query. EnsureRoot already ensured the root exists, so this resolves promptly.
            .SelectMany(existingItems => Observable.Zip(
                    ReadClaimedRoots(hub, partitions),
                    // Authoritative content-manifest read (owner round-trip, NOT the lagged `existing`
                    // query snapshot): the manifest a just-completed prior import wrote can be invisible
                    // to the eventually-consistent query, which would leave prune with an empty owned set
                    // and silently NOT prune a genuinely-removed source file. Same freshness guard as
                    // ReadClaimedRoots; absent (first import) resolves promptly to an empty set (#435).
                    ReadContentManifest(hub, source.Partition),
                    (claimedRoots, previousContentPaths) => (existingItems, claimedRoots, previousContentPaths)))
            .SelectMany(snapshot =>
            {
                var existing = snapshot.existingItems
                    .Where(n => !string.IsNullOrEmpty(n.Path))
                    .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                // Per-node incremental diff. The previous import stored each source node's token in the
                // manifest ({partition}/_Activity/import-manifest — an ActivityLog whose ReturnValue holds
                // the {path→token} map). It's a descendant of the subtree we already read, so parse it from
                // `existing` — no extra query. A node whose token still matches AND is present is upserted-
                // skipped below; a missing/empty manifest (first import, or a wipe) hashes everything as
                // changed → full import (safe). This is what makes a one-node edit re-import one node.
                var manifest = ParseManifest(
                    existing.GetValueOrDefault(ManifestPath(source.Partition)), hub.JsonSerializerOptions);

                // The content-collection file paths the source owned at the LAST import (read
                // authoritatively above from {partition}/_Activity/content-manifest). The inline content
                // mirror prunes ONLY these, so a user upload the source never tracked survives a boot
                // re-import (issue #435). Empty on first import / any parse failure → prune nothing.
                var previousContentPaths = snapshot.previousContentPaths;

                // Subtrees claimed wholesale — nothing at or under these paths is synced. A claimed
                // partition ROOT (authoritative read above) decouples its WHOLE subtree; a child claimed
                // in the snapshot decouples its own subtree.
                var excludedRoots = existing.Values
                    .Where(n => n.SyncBehavior == SyncBehavior.ExcludeThisAndChildren)
                    .Select(n => n.Path)
                    .Concat(snapshot.claimedRoots)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
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
                            return Observable.Return((Imported: 0, Failed: 0, Preserved: 0));
                        }

                        // Incremental skip: unchanged since the last import (same source token) AND the
                        // node is actually present (so a drifted/deleted node still re-imports). Skips the
                        // expensive cross-hub upsert + owner re-render. Token is over the RAW source node,
                        // matching what the manifest stored.
                        var token = PartitionSourceFingerprint.ComputeNodeToken(sourceNode, hub.JsonSerializerOptions);
                        if (target is not null
                            && manifest.TryGetValue(path, out var prevToken)
                            && string.Equals(prevToken, token, StringComparison.Ordinal))
                        {
                            logger?.LogDebug(
                                "[StaticRepoImport] {Partition}: unchanged, skipping {Path}", source.Partition, path);
                            return Observable.Return((Imported: 0, Failed: 0, Preserved: 0));
                        }

                        // Two-way conflict resolution: a node changed on the SERVER since the last sync
                        // is NOT overwritten by the repo — it is preserved here and carried back to
                        // GitHub on the next commit ("newer on the server wins → GitHub"). Only reached
                        // once the repo's copy actually differs (past the incremental-skip above), i.e. a
                        // real conflict. A forced import ignores this and overwrites (deliberate discard).
                        if (policy?.PreservesServerCopyOf(target) == true)
                        {
                            logger?.LogInformation(
                                "[StaticRepoImport] {Partition}: two-way — preserving server-newer {Path} (not overwritten).",
                                source.Partition, path);
                            NodeTypeCompilationActivity.AppendLog(hub, activityPath,
                                $"↩ Kept local change to {path} (newer on the server — commit to sync it back).", logger!);
                            return Observable.Return((Imported: 0, Failed: 0, Preserved: 1));
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
                            .Select(_ => (Imported: 1, Failed: 0, Preserved: 0))
                            .Catch<(int Imported, int Failed, int Preserved), Exception>(ex =>
                            {
                                logger?.LogWarning(ex,
                                    "[StaticRepoImport] {Partition}: upsert of {Path} failed (continuing).",
                                    source.Partition, path);
                                NodeTypeCompilationActivity.AppendLog(hub, activityPath,
                                    $"⚠ Failed to import {path}: {ex.Message}", logger!,
                                    Microsoft.Extensions.Logging.LogLevel.Warning);
                                return Observable.Return((Imported: 0, Failed: 1, Preserved: 0));
                            });
                    })
                    .ToObservable()
                    .Merge(BatchSize)
                    .Aggregate((Imported: 0, Failed: 0, Preserved: 0),
                        (acc, x) => (acc.Imported + x.Imported, acc.Failed + x.Failed, acc.Preserved + x.Preserved));

                return upserted.SelectMany(count =>
                {
                    // Prune per the partition's PartitionSyncMode. In every mode the guards hold: a
                    // pruned node is absent from the source AND still Include AND not governance
                    // (_Policy/_Access/_Activity) AND not under a claimed subtree. The MODE narrows the
                    // candidate set: FullReplace prunes every such extra (mirror the partition to the
                    // repo); Additive prunes ONLY nodes the source PREVIOUSLY owned (the prior manifest's
                    // keys) so user-added nodes survive; UpsertOnly prunes nothing. See ComputePrunableNodes.
                    var toPrune = ComputePrunableNodes(
                        existing.Values, nodes.Select(n => n.Path), manifest.Keys, excludedRoots, syncMode);

                    // Two-way: never prune a node CREATED/changed on the server since the last sync. It
                    // isn't in the repo yet, but it's a local addition to be committed back — not a stale
                    // extra to remove. A forced import prunes it (the repo state wins).
                    if (policy is { PreserveServerNewer: true, Force: false, Since: { } pruneSince })
                        toPrune = toPrune.Where(n => n.LastModified <= pruneSince).ToArray();

                    var pruned = toPrune.Count == 0
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
                        SyncContentImports(hub, source, logger).SelectMany(embedCount =>
                        // Mirror inline (byte-carrying) content into each owning node's content
                        // collection — the git-committed {Space}/content/** binaries a GitSync import
                        // supplies. Binary-safe + mirroring (writes what the repo has, prunes what the
                        // SOURCE dropped) so committed course videos/posters land and stop getting wiped,
                        // while user uploads the source never tracked are preserved (issue #435).
                        SyncInlineContent(hub, source, previousContentPaths, logger).SelectMany(inlineCount =>
                        {
                        var contentCount = embedCount + inlineCount;
                        // Persist the per-node manifest LAST (after upserts + prune) so the NEXT import's
                        // diff sees exactly what's now in the partition. One write; survives prune (_Activity).
                        return WriteManifest(hub, source.Partition, nodes, hub.JsonSerializerOptions, logger).Select(_ =>
                        {
                            // 🚨 Terminal status reflects per-file outcomes: ANY failed upsert →
                            // Warning (the ⚠ lines above pinpoint which files), all-clear →
                            // Succeeded. Written via Complete so the status + summary land in ONE
                            // atomic Update — a reader observing the terminal status always sees the
                            // full diagnostic log (no torn "Succeeded but the ⚠ lines didn't land yet").
                            var failed = count.Failed;
                            var status = failed > 0 ? ActivityStatus.Warning : ActivityStatus.Succeeded;
                            // Two-way preserved local edits (kept, not overwritten) noted only when any.
                            var preservedNote = count.Preserved > 0 ? $", kept {count.Preserved} local edit(s)" : "";
                            var summary = failed > 0
                                ? $"Imported {count.Imported} node(s), {failed} FAILED (see ⚠ above){preservedNote}, pruned {prunedCount}, synced {contentCount} content file(s)."
                                : $"Imported {count.Imported} node(s){preservedNote}, pruned {prunedCount}, synced {contentCount} content file(s).";
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
                                failed > 0 ? "ImportedWithErrors" : "Imported", count.Imported, count.Preserved);
                        });
                        })));
                });
            })
            .Catch<StaticRepoImportResult, Exception>(ex =>
            {
                NodeTypeCompilationActivity.MarkFailed(hub, activityPath, ex.Message, logger!);
                logger?.LogWarning(ex, "[StaticRepoImport] {Partition} import failed.", source.Partition);
                NotifyStartupFailure(hub, source.Partition, activityPath, ex.Message, logger);
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

    /// <summary>The <c>Markdown</c> node type — referenced by name; used for the source-owned-catalog
    /// bookkeeping markers, which are NESTED nodes and so must NOT be a partition-owning type.</summary>
    private const string MarkdownNodeTypeName = "Markdown";

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
    /// Authoritatively read each partition ROOT and return the paths claimed with
    /// <see cref="SyncBehavior.ExcludeThisAndChildren"/> ("sync: none"). Uses the authoritative
    /// single-node read (<c>GetMeshNodeStream</c>), NOT <c>meshService.Query</c>: the
    /// eventually-consistent query LAGS a just-set claim, so a freshly decoupled partition would be
    /// silently re-synced on the next import (the atioz Provider key clobber, 2026-06-25). EnsureRoot
    /// has already ensured each root exists, so each read resolves promptly.
    /// </summary>
    private static IObservable<string[]> ReadClaimedRoots(IMessageHub hub, string[] partitions)
    {
        if (partitions.Length == 0)
            return Observable.Return(Array.Empty<string>());
        var workspace = hub.GetWorkspace();
        return Observable.Zip(partitions.Select(p =>
                workspace.GetMeshNodeStream(p)
                    .Where(n => n is not null)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10))
                    .Catch((Exception _) => Observable.Return<MeshNode?>(null))))
            .Select(roots => roots
                .Where(r => r is { SyncBehavior: SyncBehavior.ExcludeThisAndChildren })
                .Select(r => r!.Path)
                .ToArray());
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
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        // 🚨 Honour an admin's "stop sync" claim on the partition ROOT. If the EXISTING root carries a
        // non-Include SyncBehavior (ExcludeThisAndChildren = "sync: none" for the whole partition),
        // leave it ENTIRELY untouched: re-materialising it from the static source would reset its
        // SyncBehavior back to Include and silently re-enable sync — clobbering the admin's decouple,
        // and (via excludedRoots in Run) re-opening overwrite of every child. The root's schema /
        // routing / admin grant already exist (it is not absent), so skipping the upsert is safe.
        // Mirrors the per-node IsClaimed skip in Run; this is what makes setting a partition root to
        // ExcludeThisAndChildren a DURABLE, partition-wide decouple ("sync: none").
        return meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{source.Partition}"))
            .Take(1)
            .SelectMany(change =>
            {
                var existing = change.Items.FirstOrDefault(n =>
                    string.Equals(n.Path, source.Partition, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    NodeTypeCompilationActivity.AppendLog(
                        hub, activityPath, $"Ensuring Space root {source.Partition}…", logger!);
                    // Absent → create through the canonical verb — creating a Space triggers eager
                    // schema provisioning + the partition-definition/routing prime + the admin grant.
                    return Upsert(hub, Materialize(root));
                }
                // EXISTING root: check the claim AUTHORITATIVELY (GetMeshNodeStream), never only the
                // query snapshot — a JUST-SET "sync: none" lags the eventually-consistent query (the
                // same race ReadClaimedRoots guards; here the stakes are higher because the upsert
                // below would re-materialize the root and RESET its SyncBehavior to Include, silently
                // re-enabling sync for the whole partition). The root exists, so the read resolves
                // promptly; ONLY on timeout fall back to the query snapshot rather than skipping the
                // import. Any other failure (access, connectivity, deserialization) propagates — an
                // unverified claim must fail the import loudly, not proceed to an upsert that could
                // clobber a claimed root.
                return hub.GetWorkspace().GetMeshNodeStream(source.Partition)
                    .Where(n => n is not null)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10))
                    .Catch((TimeoutException _) => Observable.Return<MeshNode?>(existing))
                    .SelectMany(current =>
                    {
                        if (current is { SyncBehavior: not SyncBehavior.Include })
                        {
                            logger?.LogDebug(
                                "[StaticRepoImport] {Partition}: root claimed (SyncBehavior={Behavior}) — leaving untouched.",
                                source.Partition, current.SyncBehavior);
                            return Observable.Return(0);
                        }
                        NodeTypeCompilationActivity.AppendLog(
                            hub, activityPath, $"Ensuring Space root {source.Partition}…", logger!);
                        // Existing (Include) root → overwrite to refresh the welcome, preserving
                        // owner identity. Same canonical path as every other node.
                        return Upsert(hub, Materialize(root));
                    });
            });
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

    /// <summary>
    /// Mirrors the source's inline content syncs (<see cref="IStaticRepoSource.EnumerateInlineContentSyncs"/>)
    /// — the byte-carrying content-collection files a GitSync import supplies (the git-committed
    /// <c>{Space}/content/**</c> binaries). Each group is posted as a <see cref="SyncContentFilesRequest"/>
    /// (via <see cref="ContentImportExtensions.SyncContentFiles"/>) under System to the owning node's hub,
    /// which writes the bytes and — mirroring — prunes files the SOURCE no longer carries.
    /// <para>🚨 issue #435: the mirror prunes ONLY files the source PREVIOUSLY owned
    /// (<paramref name="previouslyOwnedContentPaths"/> = the prior content manifest), so a user upload the
    /// source never tracked survives a boot re-import — the content-file analogue of the per-node
    /// <c>Additive</c>/<c>SyncBehavior</c> prune protection. After the mirror, the CURRENT source-owned
    /// paths are persisted as the content manifest so the next import knows what it owns.</para>
    /// Per-group failures log and continue. Returns the total files written.
    /// </summary>
    private static IObservable<int> SyncInlineContent(
        IMessageHub hub, IStaticRepoSource source,
        IReadOnlyList<string> previouslyOwnedContentPaths, ILogger? logger)
    {
        var syncs = source.EnumerateInlineContentSyncs();
        if (syncs.Count == 0)
            // No content this import → don't touch the collection (never an empty mirror that would wipe
            // user uploads) and leave the prior content manifest intact (conservative — a removed-to-zero
            // source set is not pruned, matching ContentAssetMapper's zero-asset behavior).
            return Observable.Return(0);

        // The full collection-relative paths this source owns THIS import. Persisted as the content
        // manifest below so the NEXT import can tell a genuinely-removed source file (prune) from a user
        // upload the source never tracked (preserve). Same {TargetPath}/{file} form the mirror compares.
        var currentOwned = syncs
            .SelectMany(s => s.Files.Select(f => CombineContentPath(s.TargetPath, f.Path)))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return syncs
            .Select(sync => AsSystem(hub, () => hub.SyncContentFiles(sync.NodePath)
                    .To(sync.TargetCollection, sync.TargetPath)
                    .Add(sync.Files)
                    .Mirror(true)
                    // Prune only files the source PREVIOUSLY owned — preserves user uploads (#435).
                    .SourceOwned(previouslyOwnedContentPaths)
                    .Post())
                .Select(r => r.Success ? r.FilesImported : 0)
                .Catch<int, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[StaticRepoImport] {Partition}: inline content sync for {Node} failed (continuing).",
                        source.Partition, sync.NodePath);
                    return Observable.Return(0);
                }))
            .ToObservable()
            .Merge(BatchSize)
            .Sum()
            // Persist the current source-owned content set LAST (after the mirror) so the next import's
            // prune preserves user uploads. Best-effort: a failed write only makes the next import prune
            // nothing (conservative), never wrong.
            .SelectMany(written => WriteContentManifest(hub, source.Partition, currentOwned, logger)
                .Select(_ => written));
    }

    /// <summary>
    /// Joins a content sync's <paramref name="targetPath"/> (the collection sub-folder) with a file's
    /// <paramref name="filePath"/> (relative to it) into the collection-relative path the mirror compares
    /// against — forward slashes, no leading slash. Mirrors <c>SyncFiles.FullPath</c> in the handler so the
    /// persisted manifest lines up exactly with what the mirror enumerates and keeps.
    /// </summary>
    private static string CombineContentPath(string? targetPath, string? filePath)
    {
        var baseDir = (targetPath ?? string.Empty).Replace('\\', '/').Trim('/');
        var rel = (filePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return baseDir.Length == 0 ? rel : $"{baseDir}/{rel}";
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
    /// Surfaces a boot/startup import failure LOUDLY in the GUI. The activity at
    /// <paramref name="activityPath"/> already carries <c>Status=Failed</c> + the error
    /// (<c>MarkFailed</c>); this additionally raises a bell <see cref="NotificationService"/>
    /// Notification whose <c>TargetNodePath</c> LINKS to that activity log — so a failed boot
    /// import is something the operator SEES (a notification → click → activity log) instead of a
    /// silent wedge they must dig pod logs for. Created under System (the boot-import identity) as a
    /// satellite of the failing partition (the same owner the activity lives under — routed to that
    /// per-node hub, never the mesh hub). Fire-and-forget: a notification hiccup must never fail the
    /// import, so the error arm only logs.
    /// </summary>
    private static void NotifyStartupFailure(
        IMessageHub hub, string partition, string activityPath, string error, ILogger? logger)
    {
        var meshService = hub.ServiceProvider.GetService<IMeshService>();
        if (meshService is null)
            return;
        AsSystem(hub, () => NotificationService.CreateNotification(
                meshService,
                mainNodePath: partition,
                title: $"Startup import failed: {partition}",
                message: string.IsNullOrWhiteSpace(error) ? "Import failed during startup." : error,
                // System (not General): the platform/system-event category — the bell renders it
                // with error styling. No icon override: a bare Fluent name ("ErrorCircle") is not a
                // URL and rendered as a broken <img>; the type-based default icon is correct.
                type: NotificationType.System,
                targetNodePath: activityPath,
                createdBy: "system-security"))
            .Subscribe(
                _ => logger?.LogInformation(
                    "[StaticRepoImport] raised startup-failure notification for {Partition} → {ActivityPath}",
                    partition, activityPath),
                ex => logger?.LogWarning(ex,
                    "[StaticRepoImport] could not raise startup-failure notification for {Partition} (failure already in activity log {ActivityPath})",
                    partition, activityPath));
    }

    private const string ManifestId = "import-manifest";

    /// <summary>Path of a partition's per-node import manifest (an <c>_Activity</c> node; survives prune).</summary>
    private static string ManifestPath(string partition) => $"{partition}/_Activity/{ManifestId}";

    private const string ContentManifestId = "content-manifest";

    /// <summary>
    /// Path of a partition's content-file manifest — an <c>_Activity</c> node (survives prune, exempt
    /// from governance pruning) whose <see cref="ActivityLog.ReturnValue"/> holds the collection-relative
    /// paths the source owned at the last import. Read on the next import to prune only genuinely-removed
    /// source files, preserving user uploads (issue #435).
    /// </summary>
    private static string ContentManifestPath(string partition) => $"{partition}/_Activity/{ContentManifestId}";

    /// <summary>
    /// Reads a partition's content manifest AUTHORITATIVELY (the owner round-trip via
    /// <see cref="MeshNodeStreamExtensions.GetMeshNode"/>, NOT the eventually-consistent query snapshot
    /// which can miss the manifest a just-completed import wrote — the same freshness guard as
    /// <see cref="ReadClaimedRoots"/>). Absent (first import) or any read failure resolves promptly to an
    /// empty set, so the next mirror prunes nothing rather than wrongly (issue #435).
    /// </summary>
    private static IObservable<IReadOnlyList<string>> ReadContentManifest(IMessageHub hub, string partition)
        => hub.GetMeshNode(ContentManifestPath(partition), TimeSpan.FromSeconds(10))
            .Catch((Exception _) => Observable.Return<MeshNode?>(null))
            .Take(1)
            .Select(node => ParseContentManifest(node, hub.JsonSerializerOptions));

    /// <summary>
    /// Parses the list of source-owned content-collection paths a prior import stored in the content
    /// manifest node's <see cref="ActivityLog.ReturnValue"/>. Empty on absence / any parse failure → the
    /// next import's mirror prunes nothing (conservative — never wipes a user upload), never wrong.
    /// </summary>
    private static IReadOnlyList<string> ParseContentManifest(MeshNode? manifestNode, JsonSerializerOptions opts)
    {
        if (manifestNode is null) return Array.Empty<string>();
        try
        {
            var log = manifestNode.ContentAs<ActivityLog>(opts);
            if (log?.ReturnValue is not { } rv) return Array.Empty<string>();
            var list = rv.Deserialize<List<string>>(opts);
            return list is null ? Array.Empty<string>() : list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Writes the partition's content-file manifest — an <c>_Activity</c> node whose
    /// <see cref="ActivityLog.ReturnValue"/> is the list of collection-relative paths the CURRENT source
    /// owns. Read back on the next import so the mirror prunes only genuinely-removed source files and
    /// preserves user uploads (issue #435). Best-effort: a failed write only makes the next import's
    /// mirror prune nothing, never a user upload.
    /// </summary>
    private static IObservable<int> WriteContentManifest(
        IMessageHub hub, string partition, IReadOnlyList<string> ownedPaths, ILogger? logger)
    {
        var node = new MeshNode(ContentManifestId, $"{partition}/_Activity")
        {
            Name = $"Content manifest ({partition})",
            NodeType = ActivityNodeType.NodeType,
            MainNode = partition,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.Import)
            {
                Status = ActivityStatus.Succeeded,
                ReturnValue = JsonSerializer.SerializeToElement(ownedPaths, hub.JsonSerializerOptions),
            },
        };

        return Upsert(hub, node)
            .Catch<int, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[StaticRepoImport] {Partition}: content manifest write failed (next import's mirror prunes nothing).", partition);
                return Observable.Return(0);
            });
    }

    /// <summary>
    /// Parses the <c>{path → source-token}</c> map a prior import stored in the manifest node's
    /// <see cref="ActivityLog.ReturnValue"/>. Empty on absence / any parse failure → the next import is a
    /// full (non-incremental) import, never a wrong one.
    /// </summary>
    private static ImmutableDictionary<string, string> ParseManifest(MeshNode? manifestNode, JsonSerializerOptions opts)
    {
        if (manifestNode is null) return ImmutableDictionary<string, string>.Empty;
        try
        {
            var log = manifestNode.ContentAs<ActivityLog>(opts);
            if (log?.ReturnValue is not { } rv) return ImmutableDictionary<string, string>.Empty;
            var map = rv.Deserialize<Dictionary<string, string>>(opts);
            return map is null
                ? ImmutableDictionary<string, string>.Empty
                : map.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return ImmutableDictionary<string, string>.Empty;
        }
    }

    /// <summary>
    /// Writes the partition's per-node import manifest — an <c>_Activity</c> node whose
    /// <see cref="ActivityLog.ReturnValue"/> is the <c>{path → source-token}</c> map for the CURRENT source
    /// set. Read back on the next import to upsert only the delta. Best-effort: a failed manifest write only
    /// makes the next import non-incremental, never incorrect.
    /// </summary>
    private static IObservable<int> WriteManifest(
        IMessageHub hub, string partition, IReadOnlyList<MeshNode> nodes, JsonSerializerOptions opts, ILogger? logger)
    {
        var map = nodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g => PartitionSourceFingerprint.ComputeNodeToken(g.First(), opts),
                StringComparer.OrdinalIgnoreCase);

        var node = new MeshNode(ManifestId, $"{partition}/_Activity")
        {
            Name = $"Import manifest ({partition})",
            NodeType = ActivityNodeType.NodeType,
            MainNode = partition,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.Import)
            {
                Status = ActivityStatus.Succeeded,
                ReturnValue = JsonSerializer.SerializeToElement((IReadOnlyDictionary<string, string>)map, opts),
            },
        };

        return Upsert(hub, node)
            .Catch<int, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[StaticRepoImport] {Partition}: manifest write failed (next import non-incremental).", partition);
                return Observable.Return(0);
            });
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
            // "Node already exists" is SUCCESS for an idempotent upsert: the node is present, which IS
            // the goal. CreateOrUpdate emits it when its eventually-consistent exists-check lags a
            // recent create and falls through to CreateNode — e.g. re-importing a partition whose
            // root Space was left by a partial prior run (the atioz Harness/Command "Upsert of
            // 'Harness' failed: Node already exists" wedge). Treat it as done; a later consistent run
            // updates changed content. Any OTHER error still faults.
            .SelectMany(resp => resp.Success
                    || (resp.Error?.Contains("already exists", StringComparison.OrdinalIgnoreCase) ?? false)
                ? Observable.Return(1)
                : Observable.Throw<int>(new InvalidOperationException(
                    $"Upsert of '{node.Path}' failed: {resp.Error}")));
}

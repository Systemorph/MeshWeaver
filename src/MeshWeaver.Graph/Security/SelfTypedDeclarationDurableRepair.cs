using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Heals, once at startup, the DURABLE rows that
/// <see cref="NodeTypeDeclarationSelfTypingValidator"/> can only refuse going FORWARD: a NodeType
/// declaration persisted with <see cref="MeshNode.NodeType"/> naming ITSELF — enrolled in its own
/// instance query — is retyped to <see cref="MeshNode.NodeTypePath"/>, exactly the correction
/// #2245 applied to the static registrations.
///
/// <para><b>Why the write guard and the static retype were not enough (#2425 / #2506).</b> The
/// three built-in declarations (<c>User</c>, <c>VUser</c>, <c>Partition</c>) shipped self-typed
/// and were PERSISTED that way on live stores. #2245 fixed the static registrations and #2378
/// refuses any new write carrying the collision — but reads stay bad-data tolerant by design, so
/// a row already persisted with <c>nodeType: "User"</c> keeps answering every
/// <c>nodeType:User</c> query beside the real accounts, forever. On production that was ~600
/// <c>As&lt;User&gt; for User: value is NodeTypeDefinition</c> errors per hour from
/// <c>UserIdentityCache</c>, on an image that already contained both fixes: the defect had moved
/// from the CODE into the DATA, and only a repair of the data closes it.</para>
///
/// <para><b>Why this is a STORAGE-layer write, not <c>GetMeshNodeStream(path).Update</c>.</b> A
/// declaration path with a static claimant is served from the static registration
/// (<c>MeshDataSource.WithMeshNodes</c> seeds the per-node hub via <c>WithInitialData</c>,
/// bypassing persistence — the #2534 mechanism), so the durable row underneath is unreachable
/// through the serve path: un-readable and un-writable via the one application-level mutation
/// API. The row can only be corrected where it lives — the same seam
/// <c>LegacyUserPartitionRepair</c> already writes on. The adapter's change feed still fires, so
/// live <c>nodeType:</c> query subscriptions converge without a restart.</para>
///
/// <para><b>🚨 Where the row LIVES is where its instance query LOOKS — not where its path routes
/// (#2641).</b> The first version of this sweep read the declaration paths through the ordinary
/// path-routed <see cref="IStorageAdapter.ReadMany"/>, and on memex-cloud that healed nothing while
/// the errors kept coming at the same ~600/h: <c>User</c>'s first segment routes to the partition
/// <c>user</c>, which the V27 migration renamed to <c>auth</c> (and V31 dropped when a stray one
/// reappeared), so the path-routed probe answered "absent" — the tolerated 42P01 — and the sweep
/// wrote nothing and, worse, LOGGED nothing. The fossil is in the <c>auth</c> schema, and that is
/// precisely where every consumer finds it: <c>UserNodeType</c> pins the path-less
/// <c>nodeType:User</c> query to the <c>Auth</c> partition via a
/// <see cref="QueryRoutingRule"/>. The collision this repair exists to remove is "the declaration
/// answers its own instance query", so the row that matters is the one THAT query reaches. The
/// sweep therefore runs two lanes per declaration: the path-routed read (a row filed under its
/// own first segment), and — when the declaration's instance query is pinned to a partition by
/// the mesh's routing rules — a read of the same path INSIDE that partition, through each writable
/// <see cref="IPartitionStorageProvider"/>'s partition-scoped adapter
/// (<see cref="IPartitionStorageProvider.CreateAdapterForTable"/>, the seam the partition-storage
/// hubs address a schema through regardless of the path's first segment). A fossil found in
/// either lane is retyped through the adapter that found it, so the write lands in the schema the
/// row is in. The lanes run in sequence, each re-reading, so a row both lanes can see (a shared
/// in-memory store) is healed once and read as already-correct by the next.</para>
///
/// <para><b>Scope.</b> Candidate paths are the statically-registered declarations
/// (<see cref="Mesh.Services.StaticNodeProviderExtensions.EnumerateStaticNodes"/> filtered with
/// <see cref="NodeTypeDeclarationSelfTypingValidator.IsNodeTypeDeclarationContent"/>, so a
/// candidate whose content arrived untyped is not silently dropped) — the population that ever
/// shipped self-typed, plus any future built-in. A durable row at such a path is rewritten ONLY
/// when it matches <see cref="NodeTypeDeclarationSelfTypingValidator.IsSelfTypedDeclaration"/> —
/// the exact predicate the write guard refuses, shared so the two can never drift. Everything
/// else — already-correct declarations, real instances, package roots typed as an unrelated type
/// — is left untouched, so on a healthy store this pass reads a few rows and writes nothing.</para>
///
/// <para><b>Never silent.</b> The sweep's three outcomes — healed, tried-and-failed, nothing found
/// — used to be distinguishable only by the ABSENCE of a line for the third, which is how #2641
/// ran undiagnosed on an image that carried the repair. Every pass now ends with one Information
/// line naming the candidate paths, the pinned partitions it probed, and how many rows it read
/// and retyped, so "the repair never considered the row" reads as a count of zero, not as
/// silence.</para>
///
/// <para><b>Safe every boot.</b> A few batched reads (the same probes the URL resolver and the
/// partition hubs run constantly, so absent partitions answer "absent", never fault), zero writes
/// when nothing matches, and idempotent when something does: the healed row no longer matches the
/// predicate, and concurrent replicas retyping the same row write the same value under the
/// backend's version-conditional upsert (#971). Fire-and-forget and failure-tolerant like
/// <c>InstalledPackageRepairService</c> — a repair must never delay or fail startup; a skipped
/// pass heals on the next boot.</para>
/// </summary>
public sealed class SelfTypedDeclarationDurableRepair : IHostedService
{
    // Both fields are RELEASED the moment they are no longer needed — the hub on StartAsync, the
    // subscription when the one-shot sweep terminates. A hosted-service instance outlives the mesh
    // it belongs to (test harnesses keep the started instances to stop them at teardown), so any
    // field here that can reach the hub roots the entire DISPOSED hub graph across meshes —
    // MeshHubDisposalLeakTest names exactly this chain (started-services list → this service →
    // MessageHub) when either field is retained.
    private IMessageHub? hub;
    private SingleAssignmentDisposable? subscription;

    /// <summary>Captures the hub — held only until <see cref="StartAsync"/> consumes it.</summary>
    /// <param name="hub">Hub supplying the service provider and serializer options.</param>
    public SelfTypedDeclarationDurableRepair(IMessageHub hub) => this.hub = hub;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Take the hub ONCE and drop the field: everything below lives in locals and in the
        // chain's closures, which are released when the sweep terminates (see the gate below).
        var hub = Interlocked.Exchange(ref this.hub, null);
        if (hub is null)
            return Task.CompletedTask;

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<SelfTypedDeclarationDurableRepair>();
        // A mesh without a storage adapter has no durable rows to heal.
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Task.CompletedTask;

        var options = hub.JsonSerializerOptions;
        var staticNodes = hub.ServiceProvider.EnumerateStaticNodes().ToList();
        var declarationPaths = staticNodes
            .Where(n => NodeTypeDeclarationSelfTypingValidator.IsNodeTypeDeclarationContent(n.Content))
            .Select(n => n.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (declarationPaths.Length == 0)
        {
            logger?.LogInformation(
                "[SelfTypedDeclarationRepair] no statically-registered NodeType declaration on "
                + "this hub — nothing to sweep");
            return Task.CompletedTask;
        }

        var pinned = PinnedPartitions(
            hub.ServiceProvider.GetService<MeshConfiguration>(), staticNodes, declarationPaths, options);
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Where(p => !p.IsReadOnly)
            .ToList();
        var stats = new SweepStats();

        // Lane 1: the path-routed read — a fossil filed under its own first segment.
        var lanes = new List<IObservable<MeshNode?>>
        {
            Sweep(storage.ReadMany(declarationPaths, options), storage, "path routing", options, stats, logger),
        };
        // Lane 2: one read per (pinned partition, writable provider) — a fossil filed in the
        // partition its instance query is pinned to, addressed through the provider's
        // partition-scoped adapter so the read (and the retype) land in that schema.
        foreach (var (definition, paths) in pinned)
        {
            foreach (var provider in providers)
            {
                var scoped = provider.CreateAdapterForTable(definition, definition.Table);
                lanes.Add(Sweep(
                    scoped.ReadMany(paths, options), scoped,
                    $"partition '{definition.Namespace}' via {provider.Name}", options, stats, logger));
            }
        }

        // Subscribe-and-return per the IHostedService rule (AsynchronousCalls.md): the turn
        // returns immediately and the work belongs to the observable chain. The
        // SingleAssignmentDisposable gate makes the retained state exactly the in-flight window:
        // on a synchronously-completing store (in-memory) Finally nulls the field before the
        // gate is even armed, and arming a disposed gate disposes the inner subscription on the
        // spot — so a finished sweep leaves this instance referencing NOTHING.
        // The one line every boot ends with, whichever way the sweep terminates — the
        // "nothing found" outcome is a count of zero here, never an absent line.
        void Summarize(string outcome) => logger?.LogInformation(
            "[SelfTypedDeclarationRepair] sweep {Outcome}: {Count} declaration path(s) [{Paths}] "
            + "by path routing and inside pinned partition(s) [{Partitions}]: {Read} durable "
            + "row(s) read, {Retyped} self-typed row(s) retyped",
            outcome,
            declarationPaths.Length,
            string.Join(", ", declarationPaths),
            string.Join(", ", pinned.Select(p =>
                $"{p.Definition.Namespace}←{string.Join("|", p.Paths)}")),
            stats.Read, stats.Retyped);

        var gate = new SingleAssignmentDisposable();
        subscription = gate;
        gate.Disposable = lanes
            // Sequential, not merged: a later lane must observe an earlier lane's heal.
            .Concat()
            // A finished one-shot has nothing left to cancel, so it drops its own handle rather
            // than keeping the sweep's closures (storage, options, and through them the hub)
            // reachable from the host's IHostedService[] for the rest of the process.
            .Finally(Release)
            .Subscribe(
                _ => { },
                ex =>
                {
                    // Unreachable by construction — every lane isolates its own fault (see
                    // Sweep) — but a fault that does escape must still end with the summary,
                    // or the sweep would be silent about what it had probed before it died.
                    logger?.LogWarning(ex,
                        "[SelfTypedDeclarationRepair] declaration sweep failed; durable "
                        + "self-typed declaration rows (if any) stay unhealed until the next start");
                    Summarize("faulted");
                },
                () => Summarize("completed"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// One lane of the sweep: every durable row <paramref name="source"/> yields is counted,
    /// filtered with the shared collision predicate, and — when it is a fossil — retyped through
    /// <paramref name="target"/>, the adapter it was read from, so the write lands where the row
    /// is. A lane whose READ faults (a backend that cannot answer for one partition) is logged and
    /// ends empty, so the lanes after it still run — the sweep is per-lane tolerant for the same
    /// reason it is per-row tolerant: one failing seam must not leave every other row unhealed.
    /// </summary>
    private static IObservable<MeshNode?> Sweep(
        IObservable<MeshNode> source,
        IStorageAdapter target,
        string lane,
        JsonSerializerOptions options,
        SweepStats stats,
        ILogger? logger)
        => source
            .Do(_ => stats.CountRead())
            .Where(NodeTypeDeclarationSelfTypingValidator.IsSelfTypedDeclaration)
            .SelectMany(fossil => target
                .Write(fossil with
                {
                    NodeType = MeshNode.NodeTypePath,
                    Version = MeshNode.NextVersion(fossil.Version),
                    LastModified = DateTimeOffset.UtcNow,
                }, options)
                .Do(_ =>
                {
                    stats.CountRetyped();
                    logger?.LogInformation(
                        "[SelfTypedDeclarationRepair] retyped durable declaration row '{Path}' "
                        + "from nodeType '{Old}' to '{New}' ({Lane}) — it was enrolled in its own "
                        + "instance query (#2425/#2506/#2641)",
                        fossil.Path, fossil.NodeType, MeshNode.NodeTypePath, lane);
                })
                // Per-row tolerance: one path that cannot be written (a read-only claimant, a
                // backend hiccup) must not stop the remaining rows from healing. Logged, and
                // retried on the next boot — the fossil still matches the predicate.
                .Catch<MeshNode?, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[SelfTypedDeclarationRepair] retyping '{Path}' ({Lane}) failed; it stays "
                        + "self-typed and will be retried on the next start", fossil.Path, lane);
                    return Observable.Return<MeshNode?>(null);
                }))
            // Per-lane tolerance: a read that faults ends THIS lane, not the sweep. Logged, and
            // retried on the next boot — whatever it would have found still matches the predicate.
            .Catch<MeshNode?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[SelfTypedDeclarationRepair] reading declaration rows by {Lane} failed; any "
                    + "self-typed row there stays unhealed until the next start", lane);
                return Observable.Empty<MeshNode?>();
            });

    /// <summary>
    /// The partitions the candidate declarations' OWN instance queries are pinned to, each with
    /// the declaration paths whose <c>nodeType:{path}</c> query the mesh's
    /// <see cref="QueryRoutingRule"/>s route there — resolved exactly as the query pipeline
    /// resolves them (<see cref="MeshConfiguration.ResolveRoutingHints"/> over a path-less
    /// <c>nodeType:</c> comparison, the shape <c>UserIdentityCache.DirectoryQuery</c> has). The
    /// <see cref="PartitionDefinition"/> is the statically-registered one
    /// (<c>DefaultPartitionProvider</c>: <c>Auth</c> → schema <c>auth</c>) when there is one, else
    /// the default first-segment shape the routers synthesise.
    /// </summary>
    private static IReadOnlyList<(PartitionDefinition Definition, string[] Paths)> PinnedPartitions(
        MeshConfiguration? configuration,
        IReadOnlyList<MeshNode> staticNodes,
        string[] declarationPaths,
        JsonSerializerOptions options)
    {
        if (configuration is null || configuration.QueryRoutingRules.Count == 0)
            return [];

        var pathsByPartition = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in declarationPaths)
        {
            var instanceQuery = new ParsedQuery(
                new QueryComparison(new QueryCondition("nodeType", QueryOperator.Equal, [path])),
                TextSearch: null);
            var partition = configuration.ResolveRoutingHints(instanceQuery).Partition;
            if (string.IsNullOrEmpty(partition))
                continue;
            if (!pathsByPartition.TryGetValue(partition, out var paths))
                pathsByPartition[partition] = paths = [];
            paths.Add(path);
        }
        if (pathsByPartition.Count == 0)
            return [];

        var registered = staticNodes
            .Where(n => string.Equals(n.NodeType, PartitionNodeType.NodeType, StringComparison.Ordinal))
            .Select(n => n.ContentAs<PartitionDefinition>(options))
            .Where(d => d is not null && !string.IsNullOrEmpty(d.Namespace))
            .Select(d => d!)
            .ToList();

        return pathsByPartition
            .Select(kv => (
                Definition: registered.FirstOrDefault(d =>
                    string.Equals(d.Namespace, kv.Key, StringComparison.OrdinalIgnoreCase))
                    ?? new PartitionDefinition
                    {
                        Namespace = kv.Key,
                        Schema = kv.Key.ToLowerInvariant(),
                        TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
                        NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
                    },
                Paths: kv.Value.ToArray()))
            .ToList();
    }

    /// <summary>Counters for the end-of-sweep summary line; incremented from the sequential
    /// chain, guarded anyway because a backend may emit from its own pool thread.</summary>
    private sealed class SweepStats
    {
        private int read;
        private int retyped;

        public int Read => Volatile.Read(ref read);
        public int Retyped => Volatile.Read(ref retyped);

        public void CountRead() => Interlocked.Increment(ref read);
        public void CountRetyped() => Interlocked.Increment(ref retyped);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Release();
        return Task.CompletedTask;
    }

    /// <summary>Drops the sweep handle and, with it, everything the sweep closed over. Safe to
    /// call twice — the sweep completing and the host stopping race by construction.</summary>
    private void Release() => Interlocked.Exchange(ref subscription, null)?.Dispose();
}

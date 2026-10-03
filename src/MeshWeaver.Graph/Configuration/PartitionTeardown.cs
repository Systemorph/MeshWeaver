using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>What a whole-partition teardown did, for the caller's audit line.</summary>
/// <param name="Partition">The partition that was torn down.</param>
/// <param name="Providers">How many storage providers were asked to drop their store of it.</param>
/// <param name="RecordDeleted">Whether <c>Admin/Partition/{partition}</c> was deleted (false: it was already absent).</param>
public sealed record PartitionTeardownOutcome(string Partition, int Providers, bool RecordDeleted);

/// <summary>
/// 🚨 The DIRECT, whole-partition teardown — the platform's own partition drop, called for a
/// partition as a whole instead of as the tail of a recursive root delete.
///
/// <para><b>Why it exists.</b> The ordinary teardown (<see cref="PartitionDropPostDeletionHandler"/>)
/// only runs after a recursive <c>DeleteNodeRequest</c> of the partition root has drained the
/// subtree, and that recursive delete pre-validates EVERY descendant with a
/// <c>ValidateDeleteRequest</c> fan-out. For a whole-space deletion there is no per-node invariant
/// worth validating: the space and all its access go together. On the control instance a governed
/// <c>DeleteSpace</c> of a 31,138-descendant space stalled in exactly that fan-out
/// (<c>pre-validate-descendants</c>, 64 of 1,740 posted requests outstanding at the 25 s bound)
/// after every earlier step had run. The store drop is ONE operation per provider
/// (Postgres: <c>DROP SCHEMA … CASCADE</c>, satellite tables included) whatever the row count, so
/// its cost does not grow with the space.</para>
///
/// <para><b>Who may call it.</b> Only a caller whose <see cref="AccessService.Context"/> IS the
/// system identity — a governed, approved operation that impersonates system from its first step
/// (<see cref="Refusal"/> says so by name otherwise). It is not a user verb: a user deletes a space
/// through its root, where the delete pipeline checks their rights.</para>
///
/// <para><b>What it does, in order</b> — the same ordering contract as the ordinary teardown:</para>
/// <list type="number">
///   <item><description>claims the partition (<see cref="RecentlyDeletedRegistry.BeginSubtreeDeletion"/>),
///     so every write at or under it is refused for the duration, and tombstones its root
///     (<see cref="RecentlyDeletedRegistry.MarkDeleted"/>) so a hub re-activating on a stale snapshot
///     cannot resurrect it;</description></item>
///   <item><description>drops the store on every <see cref="IPartitionStorageProvider"/>
///     (<see cref="PartitionDropPostDeletionHandler.DropStores"/> — the same drop, never a second
///     copy of it) and evicts the process-wide queries anchored to the partition;</description></item>
///   <item><description>deletes <c>Admin/Partition/{partition}</c> as system — still inside the
///     claim, so <c>StrandedPartitionTeardownValidator</c> reads the deletion in flight and stands
///     down instead of dropping a second time.</description></item>
/// </list>
/// <para>A drop that faults lifts the tombstone, keeps the record (the retry handle) and propagates.
/// Idempotent: tearing down a partition whose store is already gone drops nothing and succeeds.</para>
///
/// <para><b>What it does NOT do.</b> It dispatches no per-node post-deletion handler — the caller
/// disposes the partition's hubs first (a governed <c>DeleteSpace</c> does, as its first step), and
/// a per-path in-memory registry keyed by a NodeType inside the partition (the compile park
/// registry) keeps its entry until the process restarts or that path is recreated.</para>
/// </summary>
public static class PartitionTeardown
{
    /// <summary>
    /// Why <paramref name="partition"/> may NOT be torn down by this hub right now, or <c>null</c>.
    /// Read at PLAN time by a governed operation so it refuses before anything is touched, and again
    /// by <see cref="TearDownPartition"/> itself. Names each reason: not the system identity, not a
    /// valid partition segment, a database-populated mirror, a partition served by configuration, a
    /// deletion of it already in flight, or a hub with neither a storage provider nor a storage adapter.
    /// </summary>
    /// <param name="hub">The hub whose services decide.</param>
    /// <param name="partition">The partition (first path segment).</param>
    /// <param name="requireSystem">Whether the current identity must be system — true when about to
    /// act; a planner running under its own impersonation passes true as well.</param>
    /// <returns>The refusal, or <c>null</c> when the teardown may run.</returns>
    public static string? Refusal(IMessageHub hub, string partition, bool requireSystem = true)
    {
        if (!PartitionDefinition.IsValidPartitionSegment(partition))
            return $"'{partition}' is not a valid partition segment — only a top-level partition can be torn down";
        if (WellKnownPartitions.IsMirror(partition))
            return $"'{partition}' is a system-managed mirror partition, populated by the database — it is never torn down";
        if (requireSystem)
        {
            var identity = hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId;
            if (!string.Equals(identity, WellKnownUsers.System, StringComparison.Ordinal))
                return $"a whole-partition teardown runs only under the system identity, and the current identity is "
                       + $"'{identity ?? "(none)"}' — a governed operation impersonates system from its first step";
        }
        if (hub.ServiceProvider.FindStaticNode(partition) is { IsDefinitionOnly: false })
            return $"'{partition}' is served by configuration (a static partition) — it has no store to drop";
        // A shipped content partition (Doc, …) is a READ-ONLY provider with a FIXED partition
        // definition; its nodes are children (Doc/Architecture), so the static-node probe above is
        // null for it. Dropping "its store" would only remove the record and leave the content.
        if (hub.ServiceProvider.GetServices<IPartitionStorageProvider>().FirstOrDefault(p =>
                p.IsReadOnly && string.Equals(p.PartitionDefinition?.Namespace, partition, StringComparison.OrdinalIgnoreCase))
            is { } fixedProvider)
            return $"'{partition}' is served by the read-only provider '{fixedProvider.Name}' (shipped content with a fixed "
                   + "partition definition) — it has no store to drop";
        if (hub.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>().IsUnderActiveDeletion(partition, out var root))
            return $"a deletion of '{root}' is already in flight — one teardown at a time";
        // A backend with no partition provider at all (a single in-memory adapter) is still torn down —
        // by the sweep below the pipeline. Only a hub with neither has nothing to act on.
        if (!hub.ServiceProvider.GetServices<IPartitionStorageProvider>().Any()
            && hub.ServiceProvider.GetService<IStorageAdapter>() is null)
            return "this hub has neither a partition storage provider nor a storage adapter, so there is no store to drop";
        return null;
    }

    /// <summary>
    /// Tears <paramref name="partition"/> down as a whole — store drop on every provider, cached
    /// queries evicted, record deleted — under the system identity the caller already holds. Cold;
    /// emits exactly once. See the class remarks for the ordering and what it does not do.
    /// </summary>
    /// <param name="hub">The hub whose providers, registry and mesh service are used.</param>
    /// <param name="partition">The partition (first path segment).</param>
    /// <param name="because">Who asked and why — carried into the log line.</param>
    /// <returns>What was done.</returns>
    public static IObservable<PartitionTeardownOutcome> TearDownPartition(
        this IMessageHub hub, string partition, string because)
        => Observable.Defer(() =>
        {
            if (Refusal(hub, partition) is { } refused)
                return Observable.Throw<PartitionTeardownOutcome>(new InvalidOperationException(refused));

            var registry = hub.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
            var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
            var access = hub.ServiceProvider.GetRequiredService<AccessService>();
            var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(PartitionTeardown));
            var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().Count();
            var recordPath = $"{PartitionNodeType.Namespace}/{partition}";

            logger?.LogWarning(
                "[PartitionTeardown] tearing down '{Partition}' as a whole, as system, across {Providers} provider(s): {Because}",
                partition, providers, because);

            // 🚨 WithinSubtreeDeletion, not Observable.Using: the outcome is the caller's "torn down"
            // signal, so the claim is released BEFORE it is delivered. Under Using it was released
            // only after the subscriber had processed it, and a follow-up teardown issued on that
            // signal (the DeleteSpace probe's cleanup) was refused as "already in flight".
            return registry.WithinSubtreeDeletion(
                partition,
                () =>
                {
                    registry.MarkDeleted(partition);
                    return StoreKnown(hub, partition)
                        .SelectMany(known => PartitionDropPostDeletionHandler.DropStores(hub, partition, because, logger)
                            .SelectMany(__ => known == true
                                ? Observable.Return(0)
                                : SweepRows(hub, partition, logger)))
                        .Do(__ => PartitionDropPostDeletionHandler.DropCachedPartitionQueries(hub, partition, logger))
                        .SelectMany(__ => access.RunAsSystem(() => mesh.DeleteNode(recordPath)).Take(1)
                            .DefaultIfEmpty(false))
                        .Select(deleted => new PartitionTeardownOutcome(partition, providers, deleted))
                        .Catch<PartitionTeardownOutcome, Exception>(ex =>
                        {
                            // The partition is still there — a standing tombstone would refuse every
                            // write into it. The record stays as the retry handle.
                            registry.Clear(partition);
                            logger?.LogError(ex,
                                "[PartitionTeardown] the teardown of '{Partition}' FAILED; its record is kept for a retry",
                                partition);
                            return Observable.Throw<PartitionTeardownOutcome>(new InvalidOperationException(
                                $"the teardown of partition '{partition}' failed — {ex.GetType().Name}: {ex.Message}", ex));
                        });
                });
        });

    /// <summary>
    /// Whether any provider KNOWS it holds a per-partition store for <paramref name="partition"/>
    /// (OR-fold of <see cref="IPartitionStorageProvider.PartitionExists"/>): true — its drop removes
    /// every row, satellites included; otherwise null/false. Never errors: a probe that faults is
    /// "cannot tell", which makes the teardown sweep rows rather than trust a drop it cannot see.
    /// </summary>
    private static IObservable<bool?> StoreKnown(IMessageHub hub, string partition)
    {
        var probes = hub.ServiceProvider.GetServices<IPartitionStorageProvider>()
            .Select(p => p.PartitionExists(partition).Take(1).DefaultIfEmpty(null)
                .Catch<bool?, Exception>(_ => Observable.Return<bool?>(null)))
            .ToList();
        return probes.Count == 0
            ? Observable.Return<bool?>(null)
            : probes.Merge().ToList().Select(answers => answers.Any(a => a == true) ? true
                : answers.Any(a => a == false) ? false : (bool?)null);
    }

    /// <summary>
    /// On a backend with NO per-partition store (the in-memory store, a single shared adapter), the
    /// provider drop removes nothing, so the partition's rows are removed below the pipeline through
    /// the storage seam — deepest first, satellites included, no per-node validation: this is the
    /// in-memory form of the schema drop, never a recursive <c>DeleteNodeRequest</c>. Emits how many
    /// rows went. A backend that reported its store present never reaches this — its drop removed them.
    /// </summary>
    private static IObservable<int> SweepRows(IMessageHub hub, string partition, ILogger? logger)
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(0);
        return storage.ListDescendantPaths(partition).Take(1)
            .SelectMany(paths =>
            {
                var ordered = paths.Append(partition)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => p.Count(c => c == '/'))
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToList();
                return storage.DeleteMany(ordered).Take(1);
            })
            .Do(removed => logger?.LogInformation(
                "[PartitionTeardown] '{Partition}' has no per-partition store on this backend — removed its {Count} row(s) through the storage seam",
                partition, removed.Count))
            .Select(removed => removed.Count)
            .DefaultIfEmpty(0);
    }
}

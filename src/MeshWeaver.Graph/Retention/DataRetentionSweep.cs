using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Retention;

/// <summary>A partition the sweep visits.</summary>
/// <param name="Name">The partition (first path segment), as its <c>Admin/Partition</c> record names it.</param>
/// <param name="Versioned">Whether the partition records version history at all.</param>
public sealed record RetentionPartition(string Name, bool Versioned);

/// <summary>What one partition's sweep removed.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Activities">Finished activities removed.</param>
/// <param name="Versions">Recorded versions removed.</param>
/// <param name="VersionsSupported">False when the version store cannot prune (nothing was removed there).</param>
/// <param name="LogEntries">Log-entry rows removed.</param>
/// <param name="DeleteFailures">Rows the policy selected whose delete failed or timed out; they stay for the next pass.</param>
/// <param name="Failures">Classes that failed in this partition this run (each named), empty when none did.</param>
/// <param name="SkipReason">Set when the partition was skipped entirely (its policy could not be resolved).</param>
public sealed record PartitionRetentionOutcome(
    string Partition, int Activities, int Versions, bool VersionsSupported, int LogEntries, int DeleteFailures,
    ImmutableList<string> Failures, string? SkipReason = null)
{
    /// <summary>A partition skipped for <paramref name="reason"/>.</summary>
    /// <param name="partition">The partition.</param>
    /// <param name="reason">Why.</param>
    public static PartitionRetentionOutcome Skipped(string partition, string reason)
        => new(partition, 0, 0, true, 0, 0, ImmutableList<string>.Empty, reason);
}

/// <summary>What one class removed in one partition: rows deleted, and rows whose delete failed.</summary>
/// <param name="Removed">Rows deleted.</param>
/// <param name="Failed">Selected rows whose delete failed or timed out.</param>
public sealed record RetentionClassResult(int Removed, int Failed);

/// <summary>
/// One pass of the data-retention sweep (<c>Doc/Architecture/DataRetention</c>): for each partition,
/// resolve its policy (<see cref="DataRetentionResolver"/>), then remove the finished activities,
/// the recorded versions and the log-entry rows that policy calls expired. Every public member
/// is cold and <see cref="IObservable{T}"/>-shaped; nothing here awaits.
///
/// <para><b>Bounded and paced.</b> Partitions run one after another (<c>Concat</c>), the classes of
/// a partition one after another, and the deletes of a class one at a time. Each class removes at
/// most its <c>MaxDeletionsPerRun</c> per partition per pass, so a backlog drains over successive
/// passes instead of one pass becoming a mass delete.</para>
///
/// <para><b>Through the framework.</b> Activities and log entries are removed with
/// <see cref="IMeshService.DeleteNode"/> (recursive, so an activity's <c>_Log</c> segments go with
/// it, and the workspace cache stays in step). Version history is removed with
/// <see cref="IVersionQuery.PruneHistory"/>, the store-level operation each store implements; history
/// rows are not nodes and are held in no cache. Never a raw SQL statement against a live portal.
/// It runs as System, because it is infrastructure acting on records nobody owns, with no user on
/// the thread.</para>
///
/// <para><b>Failure is per class, per partition.</b> A failed read or delete is logged and
/// counted; the next class and the next partition still run, and the next pass retries. A policy
/// that cannot be resolved skips the partition (see <see cref="IDataRetentionRule"/>).</para>
/// </summary>
public static class DataRetentionSweep
{
    /// <summary>The pass name: the prefix of its one outcome line.</summary>
    public const string PassName = "DataRetention";

    /// <summary>The query listing the partitions.</summary>
    public const string PartitionListQuery = "namespace:Admin/Partition nodeType:Partition limit:1000";

    /// <summary>How long one window read may take before that class is skipped for the partition.</summary>
    public static readonly TimeSpan QueryBound = TimeSpan.FromSeconds(30);

    /// <summary>How long one delete may take before that row is left for the next pass.</summary>
    public static readonly TimeSpan DeleteBound = TimeSpan.FromSeconds(15);

    /// <summary>How long one partition's history prune may take.</summary>
    public static readonly TimeSpan VersionPruneBound = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs one pass over every partition and writes its one outcome line (the "what was pruned"
    /// line). Emits the per-partition outcomes once, then completes.
    /// </summary>
    /// <param name="hub">An off-router hub that outlives the pass (<c>NodeOperationIssuingHub()</c>).</param>
    /// <param name="resolver">The per-partition policy resolver.</param>
    /// <param name="now">The instant every cutoff of this pass is taken from.</param>
    /// <param name="logger">Where the lines go.</param>
    public static IObservable<ImmutableList<PartitionRetentionOutcome>> RunPass(
        IMessageHub hub, DataRetentionResolver resolver, DateTimeOffset now, ILogger? logger)
        => ListPartitions(hub)
            .Catch<ImmutableList<RetentionPartition>, Exception>(ex =>
            {
                new PassLedger(PassName, logger).Record(PassOutcome.Aborted, ex,
                    ": the partition list could not be read, so nothing was pruned; the next pass tries again");
                return Observable.Empty<ImmutableList<RetentionPartition>>();
            })
            .SelectMany(partitions => RunPass(hub, resolver, partitions, now, logger));

    /// <summary>One pass over the given partitions, with its outcome line.</summary>
    /// <param name="hub">An off-router hub.</param>
    /// <param name="resolver">The per-partition policy resolver.</param>
    /// <param name="partitions">The partitions to sweep.</param>
    /// <param name="now">The instant every cutoff is taken from.</param>
    /// <param name="logger">Where the lines go.</param>
    public static IObservable<ImmutableList<PartitionRetentionOutcome>> RunPass(
        IMessageHub hub, DataRetentionResolver resolver, IReadOnlyList<RetentionPartition> partitions,
        DateTimeOffset now, ILogger? logger)
        => partitions
            .Select(p => !VersionHistoryPrune.IsPartitionToken(p.Name)
                ? RefuseName(p.Name, logger)
                : resolver.Resolve(p.Name)
                .SelectMany(policy => SweepPartition(hub, policy, p.Versioned, now, logger))
                .Catch<PartitionRetentionOutcome, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Data retention: skipped partition {Partition} — its retention policy could not be resolved, so "
                        + "nothing was removed there (falling back to the instance policy could delete data a rule keeps longer)",
                        p.Name);
                    return Observable.Return(PartitionRetentionOutcome.Skipped(p.Name, ex.Message));
                }))
            .Concat()
            .ToList()
            .Select(outcomes =>
            {
                var list = outcomes.ToImmutableList();
                Report(list, logger);
                return list;
            });

    /// <summary>
    /// A partition whose name is not a single path token (<see cref="VersionHistoryPrune.IsPartitionToken"/>)
    /// is skipped: its name goes into queries and store walks run as System, where whitespace, query
    /// grammar or a traversal name would change what they select.
    /// </summary>
    private static IObservable<PartitionRetentionOutcome> RefuseName(string partition, ILogger? logger)
    {
        logger?.LogWarning(
            "Data retention: skipped partition {Partition} — its name is not a single path token (letters, digits, "
            + "_ - . @; no leading - or .), so no query or store walk is built from it", partition);
        return Observable.Return(PartitionRetentionOutcome.Skipped(partition, "not a single path token"));
    }

    /// <summary>
    /// The partitions to sweep, read as System from <c>Admin/Partition</c>. A record whose content
    /// does not type is still swept (its name is its id) and treated as versioned: the history prune
    /// of a partition that records none removes nothing.
    /// </summary>
    /// <param name="hub">An off-router hub.</param>
    public static IObservable<ImmutableList<RetentionPartition>> ListPartitions(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        var options = hub.JsonSerializerOptions;
        return access.RunAsSystem(() => mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery(PartitionListQuery))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Timeout(QueryBound))
            .Select(change => change.Items
                .Select(node =>
                {
                    var definition = node.ContentAs<PartitionDefinition>(options);
                    var name = definition?.Namespace is { Length: > 0 } ns ? ns : node.Id;
                    return new RetentionPartition(name.Trim().Trim('/'), definition?.Versioned ?? true);
                })
                // Only empty names are dropped here; any other unusable name reaches RunPass, which
                // skips it with a Warning, so a malformed record is visible rather than silently absent.
                .Where(p => p.Name.Length > 0)
                .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToImmutableList());
    }

    /// <summary>
    /// Sweeps one partition under its resolved policy. Emits one outcome, then completes; never errors
    /// (each class's failure is in <see cref="PartitionRetentionOutcome.Failures"/>).
    /// </summary>
    /// <param name="hub">An off-router hub.</param>
    /// <param name="policy">The policy in force for the partition.</param>
    /// <param name="versioned">Whether the partition records history.</param>
    /// <param name="now">The instant every cutoff is taken from.</param>
    /// <param name="logger">Where the lines go.</param>
    public static IObservable<PartitionRetentionOutcome> SweepPartition(
        IMessageHub hub, ResolvedDataRetention policy, bool versioned, DateTimeOffset now, ILogger? logger)
    {
        var partition = policy.Partition;
        var options = hub.JsonSerializerOptions;

        var activities = policy.Activities.Enabled
            ? RemoveExpired(hub, policy.Activities.WindowQuery(partition), policy.Activities.MaxDeletionsPerRun,
                node => policy.Activities.IsExpired(node, now, options), "activities", partition, logger)
            : Observable.Return<RetentionClassResult?>(new RetentionClassResult(0, 0));
        var logEntries = policy.LogEntries.Enabled
            ? RemoveExpired(hub, policy.LogEntries.WindowQuery(partition), policy.LogEntries.MaxDeletionsPerRun,
                node => policy.LogEntries.IsExpired(node, now), "log entries", partition, logger)
            : Observable.Return<RetentionClassResult?>(new RetentionClassResult(0, 0));
        var versions = policy.Versions.Enabled && versioned
            ? PruneVersions(hub, partition, policy.Versions.ToPrune(now), logger)
            : Observable.Return<VersionHistoryPruneOutcome?>(VersionHistoryPruneOutcome.Pruned(0));

        return activities.SelectMany(a => versions.SelectMany(v => logEntries.Select(l =>
        {
            var failures = ImmutableList<string>.Empty;
            if (a is null) failures = failures.Add("activities");
            if (v is null) failures = failures.Add("versions");
            if (l is null) failures = failures.Add("log entries");
            var outcome = new PartitionRetentionOutcome(
                partition, a?.Removed ?? 0, v?.Removed ?? 0, v?.Supported ?? true, l?.Removed ?? 0,
                (a?.Failed ?? 0) + (l?.Failed ?? 0), failures);
            if (outcome.Activities + outcome.Versions + outcome.LogEntries > 0)
                // Information, deliberately: this records the platform removing rows, and an
                // operator must be able to find it. One line per partition per pass, and only when
                // something was removed.
                logger?.LogInformation(
                    "Data retention: pruned {Partition} — {Activities} finished activities older than {ActivityMaxAge} ({ActivitySource}), "
                    + "{Versions} versions older than {VersionMaxAge} beyond the newest {KeepLast} ({VersionSource}), "
                    + "{LogEntries} {LogEntryType} rows older than {LogEntryMaxAge} ({LogEntrySource})",
                    partition,
                    outcome.Activities, policy.Activities.MaxAge, policy.ActivitiesSource,
                    outcome.Versions, policy.Versions.MaxAge, policy.Versions.KeepLast, policy.VersionsSource,
                    outcome.LogEntries, policy.LogEntries.NodeType, policy.LogEntries.MaxAge, policy.LogEntriesSource);
            return outcome;
        })));
    }

    /// <summary>
    /// Reads the oldest window of <paramref name="query"/>, keeps the rows <paramref name="isExpired"/>
    /// calls expired (at most <paramref name="max"/>), and deletes them one at a time. Emits the
    /// number removed and the number whose delete failed, or null when the window could not be read.
    /// <para>The query bounds the work; the predicate decides. A backend that ignored the sort would
    /// only drain slower, never delete a row the policy has not called expired.</para>
    /// </summary>
    private static IObservable<RetentionClassResult?> RemoveExpired(
        IMessageHub hub, string query, int max, Func<MeshNode, bool> isExpired,
        string what, string partition, ILogger? logger)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetService<AccessService>();
        return access.RunAsSystem(() => mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery(query))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Take(1)
                .Timeout(QueryBound))
            .Select(change => change.Items
                .Where(isExpired)
                .Select(n => n.Path)
                .Distinct(StringComparer.Ordinal)
                .Take(max)
                .ToImmutableList())
            .SelectMany(paths => paths.IsEmpty
                ? Observable.Return(new RetentionClassResult(0, 0))
                : paths
                    .Select(path => access.RunAsSystem(() => mesh.DeleteNode(path))
                        .Take(1)
                        .Timeout(DeleteBound)
                        // false = already gone (an idempotent delete): neither removed nor failed.
                        .Select(removed => new RetentionClassResult(removed ? 1 : 0, 0))
                        .DefaultIfEmpty(new RetentionClassResult(0, 0))
                        .Catch<RetentionClassResult, Exception>(ex =>
                        {
                            logger?.LogWarning(ex,
                                "Data retention: {Path} ({What}) was selected but its delete failed or timed out; it stays, "
                                + "and the next pass retries it", path, what);
                            return Observable.Return(new RetentionClassResult(0, 1));
                        }))
                    .Concat()
                    .Aggregate(new RetentionClassResult(0, 0),
                        (sum, r) => new RetentionClassResult(sum.Removed + r.Removed, sum.Failed + r.Failed)))
            .Select(r => (RetentionClassResult?)r)
            .Catch<RetentionClassResult?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Data retention: the {What} window of {Partition} could not be read; nothing of it was removed this pass",
                    what, partition);
                return Observable.Return<RetentionClassResult?>(null);
            });
    }

    private static IObservable<VersionHistoryPruneOutcome?> PruneVersions(
        IMessageHub hub, string partition, VersionHistoryPrune prune, ILogger? logger)
    {
        var versions = hub.ServiceProvider.GetService<IVersionQuery>();
        if (versions is null)
            return Observable.Return<VersionHistoryPruneOutcome?>(VersionHistoryPruneOutcome.Pruned(0));
        return versions.PruneHistory(partition, prune)
            .Take(1)
            .Timeout(VersionPruneBound)
            .DefaultIfEmpty(VersionHistoryPruneOutcome.NotSupported)
            .Select(o => (VersionHistoryPruneOutcome?)o)
            .Catch<VersionHistoryPruneOutcome?, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "Data retention: the version history of {Partition} could not be pruned; nothing of it was removed this pass",
                    partition);
                return Observable.Return<VersionHistoryPruneOutcome?>(null);
            });
    }

    /// <summary>Writes the pass's one outcome line: what was pruned, over how many partitions, and what could not be.</summary>
    private static void Report(ImmutableList<PartitionRetentionOutcome> outcomes, ILogger? logger)
    {
        var ledger = new PassLedger(PassName, logger);
        var skipped = outcomes.Where(o => o.SkipReason is not null).Select(o => o.Partition).ToImmutableList();
        var failed = outcomes.Where(o => !o.Failures.IsEmpty)
            .Select(o => $"{o.Partition} ({string.Join(", ", o.Failures)})").ToImmutableList();
        var unsupported = outcomes.Where(o => !o.VersionsSupported).Select(o => o.Partition).ToImmutableList();
        if (outcomes.IsEmpty)
        {
            ledger.Record(PassOutcome.Skipped, ": no partition was listed, so nothing was pruned");
            return;
        }
        ledger.Record(PassOutcome.Collected,
            ": what was pruned — {Activities} finished activities, {Versions} recorded versions, {LogEntries} log entries, "
            + "over {Partitions} partitions; {DeleteFailures} selected rows whose delete failed (kept for the next pass); "
            + "skipped (unusable name or policy unresolved): [{Skipped}]; failed classes: [{Failed}]; "
            + "version history not prunable by this store: [{Unsupported}]",
            outcomes.Sum(o => o.Activities), outcomes.Sum(o => o.Versions), outcomes.Sum(o => o.LogEntries),
            outcomes.Count, outcomes.Sum(o => o.DeleteFailures), string.Join(", ", skipped), string.Join(", ", failed), string.Join(", ", unsupported));
    }
}

using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// The ONE place that asks the STORAGE PROVIDERS whether a partition still exists, and the only
/// witness in the mesh that is independent of the node index.
///
/// <para>🚨 <b>Why an independent witness is the whole point.</b> Every other absence test in the
/// platform asks the index — a listing (<c>path:…</c>, <c>scope:children</c>) or a query — and an
/// index row is exactly what survives a partition being torn down. A row and the check that would
/// disprove it then come from one source, so a stale row CONFIRMS ITSELF: the sweep enumerates a
/// NodeType from a partition whose store was dropped, the listing it asks about that NodeType
/// answers from the same stale index, and absence is not merely unproven but unaskable. That is
/// the shape behind <see href="https://github.com/Systemorph/MeshWeaver/issues/5073">#5073</see>
/// (a new pod's bake pumping a partition deleted long ago) and behind the older pattern of a
/// NodeType issue closed on a <c>Not found</c> and re-filed unchanged weeks later. The provider
/// answers from the store itself, which is the thing that was actually dropped.</para>
///
/// <para><b>Absence is CONFIRMED, never inferred.</b> An indeterminate probe (<c>null</c>: a
/// provider with no per-partition store, a probe that timed out, a probe that errored, a provider
/// that threw) never confirms absence: it fails OPEN, so every caller keeps its previous behaviour
/// byte-for-byte wherever the question cannot be answered. The direction matters and is not
/// symmetric — a false "absent" would make the platform skip real work, while a false "present"
/// merely costs what it costs today.</para>
///
/// <para>🚨 <b>ONE fan-out, TWO folds, and they are not interchangeable.</b> <see cref="Probe"/> is
/// the shared measurement; what counts as "gone" depends on what the caller will DO with the
/// answer, so the fold lives at the call site and each one says which it is:</para>
/// <list type="bullet">
///   <item><description><see cref="ConfirmedAbsent"/> — <b>EVERY</b> provider says <c>false</c>.
///     The conservative fold, and the one every caller that will SKIP OR REFUSE WORK must use. It
///     is the fold <c>PartitionWriteGuardValidator</c> already established, for a reason worth
///     repeating in full: <i>a single <c>false</c> means "not in MY store", NOT "doesn't exist
///     anywhere"</i> — the Postgres provider answers <c>false</c> for a filesystem-backed
///     partition while the FileSystem provider that actually holds it cannot answer and returns
///     <c>null</c>. A non-owning provider must never veto a partition another provider
///     owns.</description></item>
///   <item><description>The looser <i>"someone says false and nobody says true"</i> fold, which
///     <see cref="PathResolutionService"/> applies inline to decide whether to synthesize a
///     placeholder partition ROOT. That is a different question whose safe direction is the other
///     way round, so it keeps its shipped semantics — and it is deliberately NOT offered here,
///     because a new caller reaching for it would almost certainly want the strict
///     one.</description></item>
/// </list>
/// </summary>
public static class PartitionExistenceProbe
{
    /// <summary>
    /// How long one provider's probe may take before it is read as INDETERMINATE (never as
    /// absent). A provider that cannot answer promptly has not answered, and the fail-open rule
    /// covers the rest.
    /// </summary>
    public static readonly TimeSpan DefaultProbeBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The first path segment — the partition a mesh path lives in — or the empty string when
    /// there is none. <c>UWDeepfield/IndustryNews/_Thread/x</c> → <c>UWDeepfield</c>.
    /// </summary>
    /// <param name="path">Any mesh path.</param>
    public static string PartitionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var slash = path.IndexOf('/');
        return slash < 0 ? path : path[..slash];
    }

    /// <summary>
    /// THE shared measurement: one vote per writable provider, in registration order. Emits ONE
    /// list and completes; never faults, and never completes without emitting.
    ///
    /// <para>Each vote is <c>true</c> (this provider's store has it), <c>false</c> (this provider
    /// knows its store and it is not there) or <c>null</c> (it cannot tell). An empty
    /// <paramref name="writableProviders"/> yields an empty list — which every fold must read as
    /// "nothing was established", never as absence.</para>
    ///
    /// <para>🚨 Three ways a provider can fail to answer, and all three are normalised to
    /// <c>null</c> HERE so no fold has to know about them:</para>
    /// <list type="bullet">
    ///   <item><description><b>It throws synchronously.</b> <c>PartitionExists</c> is an interface
    ///     method on an extension point, so it may throw instead of returning a faulted observable
    ///     — and without the <c>Defer</c> below that throw happens while this list is being BUILT,
    ///     escaping the per-provider <c>Catch</c>, this method, and every <c>Catch</c> the caller
    ///     wrapped around the returned observable. On the bake path it would fault the sweep and
    ///     refuse the pod's readiness — an optimisation taking a pod out of rotation. Caught by
    ///     review on <see href="https://github.com/Systemorph/MeshWeaver/pull/5163">#5163</see>.</description></item>
    ///   <item><description><b>It faults the observable</b> — the ordinary case, and what the
    ///     <c>Catch</c> was always for.</description></item>
    ///   <item><description><b>It completes without emitting.</b> The contract says exactly one
    ///     value, but <c>CombineLatest</c> never emits if ANY source completes empty and
    ///     <c>Timeout</c> does not fire on an empty completion — so one silently-empty provider
    ///     would make this complete without a value, and a caller composing work behind it with
    ///     <c>SelectMany</c> would do NOTHING. On the bake path that is a sweep emitting no
    ///     outcomes and completing normally, which the gate certifies as a clean bake: the
    ///     "finding nothing is not passing" laundering <c>WarmDynamicTypes</c> faults an
    ///     enumeration error to avoid. An answer that can never arrive is settled from the
    ///     known-terminal state rather than parked.</description></item>
    /// </list>
    /// </summary>
    /// <param name="writableProviders">The writable partition providers (read-only seeds own no store and must be excluded by the caller).</param>
    /// <param name="partition">The partition name (a bare first segment).</param>
    /// <param name="logger">Optional; records a probe that could not answer, at Debug.</param>
    /// <param name="probeBudget">Per-provider ceiling; <see cref="DefaultProbeBudget"/> when null.</param>
    public static IObservable<IList<bool?>> Probe(
        IReadOnlyCollection<IPartitionStorageProvider> writableProviders,
        string partition,
        ILogger? logger = null,
        TimeSpan? probeBudget = null)
    {
        ArgumentNullException.ThrowIfNull(writableProviders);
        if (writableProviders.Count == 0 || string.IsNullOrWhiteSpace(partition))
            return Observable.Return<IList<bool?>>([]);

        var budget = probeBudget ?? DefaultProbeBudget;
        var probes = writableProviders
            // Defer, so a provider that throws SYNCHRONOUSLY becomes an OnError the Catch below
            // sees, instead of an exception escaping this method entirely.
            .Select(p => Observable.Defer(() => p.PartitionExists(partition))
                .Take(1)
                .Timeout(budget)
                .DefaultIfEmpty(null)
                .Catch<bool?, Exception>(ex =>
                {
                    logger?.LogDebug(ex,
                        "PartitionExistenceProbe: existence probe for '{Partition}' via {Provider} "
                        + "failed; treating as indeterminate",
                        partition, p.Name);
                    return Observable.Return<bool?>(null);
                }))
            .ToList();

        return Observable.CombineLatest(probes)
            .Take(1)
            .Select(results => (IList<bool?>)results)
            // Every probe above now emits, so this cannot be reached today — and it is what keeps
            // that true if one ever stops.
            .DefaultIfEmpty([]);
    }

    /// <summary>
    /// Whether <paramref name="partition"/> is CONFIRMED ABSENT: at least one provider answered and
    /// <b>EVERY</b> provider that answered said <c>false</c>. Emits once and completes; never faults.
    ///
    /// <para>🚨 <b>This is the conservative fold, and it is the one to use for anything that will
    /// SKIP or REFUSE work.</b> A single <c>false</c> means "not in MY store", never "absent
    /// everywhere": the Postgres provider answers <c>false</c> for a filesystem-backed partition
    /// while the FileSystem provider that actually holds it returns <c>null</c>, so accepting one
    /// <c>false</c> beside an indeterminate would skip a partition that genuinely exists. Same fold,
    /// same reasoning and nearly the same comment as <c>PartitionWriteGuardValidator</c>, which is
    /// where this rule was established.</para>
    /// </summary>
    /// <param name="writableProviders">The writable partition providers.</param>
    /// <param name="partition">The partition name (a bare first segment).</param>
    /// <param name="logger">Optional; records a probe that could not answer, at Debug.</param>
    /// <param name="probeBudget">Per-provider ceiling; <see cref="DefaultProbeBudget"/> when null.</param>
    public static IObservable<bool> ConfirmedAbsent(
        IReadOnlyCollection<IPartitionStorageProvider> writableProviders,
        string partition,
        ILogger? logger = null,
        TimeSpan? probeBudget = null) =>
        Probe(writableProviders, partition, logger, probeBudget)
            .Select(results => results.Count > 0 && results.All(r => r == false));

    /// <summary>
    /// The subset of <paramref name="partitions"/> that is CONFIRMED ABSENT. Emits ONE set and
    /// completes; never faults.
    ///
    /// <para>The per-partition probes run together, so the whole answer is bounded by ONE
    /// <paramref name="probeBudget"/> rather than by their number — which is what makes it
    /// affordable in front of work that would otherwise spend a per-item budget discovering the
    /// same fact one item at a time.</para>
    /// </summary>
    /// <param name="writableProviders">The writable partition providers.</param>
    /// <param name="partitions">Partition names to ask about; duplicates and blanks are ignored.</param>
    /// <param name="logger">Optional; records a probe that could not answer, at Debug.</param>
    /// <param name="probeBudget">Per-provider ceiling; <see cref="DefaultProbeBudget"/> when null.</param>
    public static IObservable<ImmutableHashSet<string>> ConfirmedAbsentAmong(
        IReadOnlyCollection<IPartitionStorageProvider> writableProviders,
        IEnumerable<string> partitions,
        ILogger? logger = null,
        TimeSpan? probeBudget = null)
    {
        ArgumentNullException.ThrowIfNull(writableProviders);
        ArgumentNullException.ThrowIfNull(partitions);

        var asked = partitions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        if (asked.IsEmpty || writableProviders.Count == 0)
            return Observable.Return(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));

        var probes = asked
            .Select(p => ConfirmedAbsent(writableProviders, p, logger, probeBudget)
                .Select(absent => (Partition: p, Absent: absent)))
            .ToList();

        return Observable.CombineLatest(probes)
            .Take(1)
            .Select(results => results
                .Where(r => r.Absent)
                .Select(r => r.Partition)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase))
            // Same rule as ConfirmedAbsent's backstop: this must emit a set, never complete silent.
            // A caller composing work behind it with SelectMany would otherwise do nothing at all.
            .DefaultIfEmpty(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));
    }
}

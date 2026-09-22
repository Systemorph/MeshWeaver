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
/// <para><b>Absence is CONFIRMED, never inferred.</b> A partition is confirmed absent only when at
/// least one WRITABLE provider says <c>false</c> — it knows its store and the partition is not
/// there — AND none says <c>true</c>. An indeterminate probe (<c>null</c>: a provider with no
/// per-partition store, a probe that timed out, a probe that errored) never confirms absence: it
/// fails OPEN, so every caller keeps its previous behaviour byte-for-byte wherever the question
/// cannot be answered. The direction matters and is not symmetric — a false "absent" would make
/// the platform skip real work, while a false "present" merely costs what it costs today.</para>
///
/// <para>Extracted from <see cref="PathResolutionService"/>, which asked the same question to
/// decide whether to synthesize a placeholder partition root and is now one of two callers. One
/// implementation, so the two cannot drift into disagreeing about what "gone" means.</para>
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
    /// Whether <paramref name="partition"/> is CONFIRMED ABSENT by the writable providers — see the
    /// class remarks for the exact vote. Emits once and completes; never faults.
    /// </summary>
    /// <param name="writableProviders">The writable partition providers (read-only seeds own no store and must be excluded by the caller).</param>
    /// <param name="partition">The partition name (a bare first segment).</param>
    /// <param name="logger">Optional; records a probe that could not answer, at Debug.</param>
    /// <param name="probeBudget">Per-provider ceiling; <see cref="DefaultProbeBudget"/> when null.</param>
    public static IObservable<bool> ConfirmedAbsent(
        IReadOnlyCollection<IPartitionStorageProvider> writableProviders,
        string partition,
        ILogger? logger = null,
        TimeSpan? probeBudget = null)
    {
        ArgumentNullException.ThrowIfNull(writableProviders);
        // Nobody owns a store, so nobody can say anything is missing from one.
        if (writableProviders.Count == 0 || string.IsNullOrWhiteSpace(partition))
            return Observable.Return(false);

        var budget = probeBudget ?? DefaultProbeBudget;
        var probes = writableProviders
            .Select(p => p.PartitionExists(partition)
                .Take(1)
                .Timeout(budget)
                // 🚨 A PROBE THAT COMPLETES WITHOUT EMITTING is indeterminate, and saying so here is
                // load-bearing rather than defensive. The contract says a provider emits exactly one
                // value and completes — but `CombineLatest` never emits if ANY source completes
                // empty, and `Timeout` does not fire on an empty completion either. One
                // silently-empty provider would therefore make this whole probe complete without a
                // value, and the caller's `SelectMany` would then produce NOTHING: on the bake path
                // that is a sweep which emits no outcomes and completes normally, i.e. exactly the
                // "finding nothing is not passing" laundering that `WarmDynamicTypes` faults an
                // enumeration error to avoid. An answer that can never arrive is settled from the
                // known-terminal state rather than parked.
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
            .Select(results => results.Any(r => r == false) && !results.Any(r => r == true))
            // The backstop for the same rule one level up: every probe above now emits, so this
            // cannot be reached today — and it is what keeps that true if one ever stops.
            .DefaultIfEmpty(false);
    }

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

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeshWeaver.Hosting;

/// <summary>
/// What one batched source-discovery query actually did: how many changes were folded, what they
/// delivered, and the LARGEST inter-chunk gap — the discriminator MeshWeaver#3704 asks for.
/// </summary>
/// <param name="Query">The bounded query text.</param>
/// <param name="Settled">The node count the fold settled at.</param>
/// <param name="Chunks">How many <c>QueryResultChange</c> events were folded.</param>
/// <param name="Items">How many items those changes delivered in total.</param>
/// <param name="ElapsedMs">Wall time from subscription to settlement.</param>
/// <param name="LargestGapMs">The largest observed gap between two consecutive changes.</param>
/// <param name="WindowMs">The completion window the fold settles on.</param>
/// <param name="At">When the pass settled.</param>
public sealed record SourceDiscoveryPass(
    string Query,
    int Settled,
    int Chunks,
    int Items,
    double ElapsedMs,
    double LargestGapMs,
    double WindowMs,
    DateTimeOffset At)
{
    /// <summary>
    /// The largest gap as a share of the completion window. A gap WIDER than the window ends the
    /// fold early and hands the compiler a short source map — the mechanism #3704 names.
    /// </summary>
    public double GapSharePercent => WindowMs > 0 ? LargestGapMs / WindowMs * 100 : 0;

    /// <summary>This pass's one-line form, for a log or a health payload.</summary>
    public string Line =>
        $"'{Query}' settled at {Settled} node(s) from {Chunks} change(s) "
        + $"({Items} item(s) delivered) in {ElapsedMs.ToString("F0", CultureInfo.InvariantCulture)}ms; "
        + $"largest inter-chunk gap {LargestGapMs.ToString("F0", CultureInfo.InvariantCulture)}ms "
        + $"= {GapSharePercent.ToString("F0", CultureInfo.InvariantCulture)}% of the "
        + $"{WindowMs.ToString("F0", CultureInfo.InvariantCulture)}ms completion window";
}

/// <summary>
/// 🚨 <b>#3704's discriminating measurement, PUBLISHED — so it can be read with <c>curl</c> instead
/// of Loki</b> (MeshWeaver#3704).
///
/// <para>#3704 is blocked on nothing about its own mechanism either. <c>NodeTypeBatchBake</c>'s
/// <c>ChunkTiming</c> already measures exactly what the issue asks for — folded change count, per
/// chunk contribution, and the largest inter-chunk gap against the completion window — and already
/// prints which way it points. But it prints it to a LOG, and log access on this fleet is
/// break-glass, so the reading the issue turns on could not be taken by anyone authorised to take
/// it. This registry holds the same numbers in process; the host's <c>SourceDiscoveryHealthCheck</c>
/// prints them on <c>ProbeEndpoints.Health</c>.</para>
///
/// <para>🚨 <b>"No pass ran" is the NORMAL state of a warm replica, and it must still be
/// VISIBLE.</b> The batch bake only issues a discovery query when something needs building, so a
/// replica whose share is fully warm records nothing — and an entry that answered Healthy-and-silent
/// would be indistinguishable from one that was never registered. That is why the check carries
/// <c>ProbeEndpoints.CensusTag</c>: it prints its reading on <c>/health</c> whether or not the
/// reading is a problem, so "I measured nothing" and "I measured, and it was clean" are two
/// different printed sentences rather than the same silence. The STATUS is reserved for the thing
/// that is genuinely wrong — a gap that approached the window.</para>
///
/// <para>A mesh-scoped instance singleton (registered in <c>AddMeshCatalog</c>, beside
/// <see cref="ContentDegradationRegistry"/>), never static. Bounded by construction: one entry per
/// distinct query, plus a single high-water reading that only ever moves UP — so a wide gap can
/// never be erased by a narrow pass that follows it.</para>
/// </summary>
public sealed class SourceDiscoveryRegistry
{
    /// <summary>
    /// 🚨 The ONE threshold, shared by the log's warning and the health verdict, so the two can
    /// never drift apart and disagree about the same pass. It picks the LEVEL only — every number
    /// is published unconditionally, so a reader never depends on where it sits.
    /// </summary>
    public const double GapShareWarnPercent = 50;

    /// <summary>The check's name on <c>/health</c>.</summary>
    public const string HealthCheckName = "source-discovery";

    private readonly ConcurrentDictionary<string, SourceDiscoveryPass> byQuery =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Volatile reference, not a gate. Only ever replaced when the new pass's gap share is HIGHER:
    /// a benign pass must not erase the evidence of a wide one, which is the whole reason a
    /// last-value-per-query map is not enough on its own.
    /// </summary>
    private volatile SourceDiscoveryPass? widest;

    /// <summary>Records one settled discovery pass.</summary>
    /// <param name="pass">The pass.</param>
    public void Record(SourceDiscoveryPass pass)
    {
        byQuery[pass.Query] = pass;
        // Read-compare-write without a CAS loop on purpose: a lost race here can only lose the
        // high-water mark for ONE pass whose per-query entry is recorded anyway, and discovery
        // passes are issued sequentially by one sweep. A gate would buy nothing and cost the rule
        // this repository does not bend.
        var current = widest;
        if (current is null || pass.GapSharePercent > current.GapSharePercent)
            widest = pass;
    }

    /// <summary>Every distinct query's most recent pass, widest gap first.</summary>
    public ImmutableList<SourceDiscoveryPass> Snapshot() =>
        byQuery.Values.OrderByDescending(p => p.GapSharePercent).ToImmutableList();

    /// <summary>The widest gap this replica has ever observed, or <c>null</c> when nothing ran.</summary>
    public SourceDiscoveryPass? Widest => widest;

    /// <summary>
    /// 🚨 Whether any recorded pass indicts the completion rule. An EMPTY registry is not indicted —
    /// nothing ran — which is exactly why the sentence has to print anyway.
    /// </summary>
    /// <param name="widest">The widest recorded pass, or <c>null</c>.</param>
    /// <returns><c>true</c> when a gap reached <see cref="GapShareWarnPercent"/> of the window.</returns>
    public static bool IsIndicted(SourceDiscoveryPass? widest) =>
        widest is not null && widest.GapSharePercent >= GapShareWarnPercent;

    /// <summary>
    /// The one sentence an operator reads on <c>/health</c>. Pure — it is the whole publication.
    /// </summary>
    /// <param name="passes">Every distinct query's most recent pass.</param>
    /// <param name="widest">The widest gap ever observed on this replica.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(
        IReadOnlyList<SourceDiscoveryPass> passes, SourceDiscoveryPass? widest)
    {
        if (passes.Count == 0)
            return "NO source-discovery pass recorded on this replica — the batch bake issued none "
                   + "(nothing needed building, or batch bake is off). There is no inter-chunk gap "
                   + "to read here: this is an absence of measurement, NOT a clean one (#3704).";

        var verdict = IsIndicted(widest)
            ? $"a gap reached {GapShareWarnPercent.ToString("F0", CultureInfo.InvariantCulture)}% of "
              + "the completion window, which is the mechanism #3704 names — a gap WIDER than the "
              + "window ends the fold early and hands the compiler a short source map"
            : "every gap stayed well inside the completion window, so the completion rule did NOT "
              + "end any of these folds early — a short read on this replica would be upstream, in "
              + "what the providers returned";

        return $"{passes.Count} discovery query/queries measured on this replica; {verdict}. "
               + $"Widest ever: {widest?.Line ?? "(none)"}. Most recent per query: "
               + string.Join("; ", passes.Select(p => p.Line));
    }
}

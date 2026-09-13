using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;

namespace MeshWeaver.Hosting;

/// <summary>
/// What this replica's framework identity has SEALED, as last read from the published root. The
/// census DENOMINATOR: a hold is only interpretable against the publication set that produced it.
/// </summary>
/// <param name="Identity">This process's framework identity.</param>
/// <param name="PublishedRoot">The published bundle root, or <c>null</c> when this deployment
/// consumes no CI bakes — a legitimate configuration, and a different sentence from "unmeasured".</param>
/// <param name="Sealed">Every source directory under this identity, as
/// <see cref="SealedPublicationIndex.ReadFor"/> read it.</param>
/// <param name="At">When the reading was taken.</param>
public sealed record SealedPublicationReading(
    string Identity,
    string? PublishedRoot,
    ImmutableList<SealedSource> Sealed,
    DateTimeOffset At)
{
    /// <summary>The reading as one sentence: identity, then every sealed source with the
    /// repository it came from and the commit it was baked from.</summary>
    public string Line
    {
        get
        {
            if (PublishedRoot is null)
                return $"framework identity {Identity}; no published bundle root is configured, so "
                       + "this deployment consumes no CI bakes and NO source can ever be held by a seal";
            if (Sealed.Count == 0)
                return $"framework identity {Identity} has NO publication at all under the published "
                       + "root — every GitSynced source of a module-bearing repository is ungated "
                       + "(SealedSyncGate treats an unattributable seal as 'does not apply')";
            return $"framework identity {Identity} holds {Sealed.Count} publication(s): "
                   + string.Join("; ", Sealed.Select(Describe));
        }
    }

    private static string Describe(SealedSource source) =>
        $"'{source.Source}' ← {source.Repository ?? "an unrecorded repository"} @ "
        + $"{(source.SourceCommit is { Length: > 0 } c ? Short(c) : "an unknown commit")}"
        + (source.IsSealed ? " (sealed)" : $" (NOT sealed: {source.Refusal ?? "no reason recorded"})");

    private static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;
}

/// <summary>
/// One repository whose GitSynced sources the publication seal is currently holding back, and
/// since when THIS REPLICA first saw it held.
/// </summary>
/// <param name="Repository">The producing repository, <c>owner/name</c>.</param>
/// <param name="BuiltCommit">The commit of the most recent green build that was held.</param>
/// <param name="Reason">The gate's own hold reason — the same string the log line and the node's
/// <c>lastSyncNote</c> carry, so the three can never disagree about one hold.</param>
/// <param name="HeldSources">How many sync sources that delivery held.</param>
/// <param name="FirstObservedAt">🚨 When THIS PROCESS first observed the repository held — not when
/// the hold began. A restart resets it, and the census says so in those words rather than
/// overclaiming a duration it cannot know.</param>
/// <param name="LastObservedAt">The most recent delivery that was held.</param>
public sealed record SealedSyncHold(
    string Repository,
    string BuiltCommit,
    string Reason,
    int HeldSources,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt)
{
    /// <summary>How long this replica has observed the repository held.</summary>
    /// <param name="now">The reading's clock.</param>
    /// <returns>The observed duration.</returns>
    public TimeSpan ObservedFor(DateTimeOffset now) => now - FirstObservedAt;

    /// <summary>This hold's one-line form, for a log or a health payload.</summary>
    /// <param name="now">The reading's clock.</param>
    /// <returns>The sentence.</returns>
    public string Line(DateTimeOffset now) =>
        $"{Repository}: {HeldSources} GitSynced source(s) held, first seen by this replica at "
        + $"{FirstObservedAt.ToString("O", CultureInfo.InvariantCulture)} "
        + $"({Format(ObservedFor(now))} ago, this process only) — {Reason}";

    private static string Format(TimeSpan span) =>
        span < TimeSpan.Zero ? "0m"
        : span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{(int)span.TotalMinutes}m";
}

/// <summary>
/// 🚨 <b>A publication seal that stops advancing freezes every GitSynced Space of that repository,
/// and until this census nothing on a RUNNING portal said so</b> (MeshWeaver#4063).
///
/// <para><b>The mechanism, measured 2026-09-12 on both production portals.</b>
/// <c>GitHubWebhookProcessor.MatchingBuildTargets</c> asks <c>SealedSyncGate</c> (MeshWeaver.GitSync) per
/// candidate, and the gate proceeds only when a publication sealed under THIS instance's framework
/// identity is at the built commit. The identity is a property of the image the pod was last rolled
/// to; the seal's identity is a property of the platform image the repository's bake resolves. When
/// those diverge the instance still holds the LAST publication made for it, so <c>mine</c> is
/// non-empty and nothing in it is ever at a later head sha: <b>hold, on every green build,
/// unboundedly</b>. Nine hours of tagged releases never reached the meshes.</para>
///
/// <para><b>What #4065 fixed and what it did not.</b> The hold is now written onto the sync config
/// (<c>lastSyncOutcome: Held</c>), so a source no longer reads field-for-field identical to a
/// settled one. But that is a per-SPACE record an operator must already suspect something to go and
/// read, one node at a time, and the boot-time reconciler is the only thing that re-evaluates a
/// hold at all. Nothing anywhere states the instance-level fact: <i>this identity has no
/// publication of repository X at or after its last green build</i>. This registry holds exactly
/// that, and the host's <c>SealedSyncHealthCheck</c> prints it on <c>ProbeEndpoints.Health</c> —
/// public, unauthenticated, composed by the process and therefore past RLS.</para>
///
/// <para>🚨 <b>It carries <c>ProbeEndpoints.CensusTag</c>, because the CLEAN reading is the whole
/// point.</b> A freeze is indistinguishable from a quiet week: both are "no import happened". An
/// entry that printed only while unhappy would be byte-identical on the wire to one that was never
/// registered, so "no green build has reached this replica", "green builds arrived and none was
/// held", and "a repository has been held for nine hours" must be THREE different printed
/// sentences. <see cref="Describe"/> is where that is guaranteed.</para>
///
/// <para>A mesh-scoped instance singleton (registered in <c>AddMeshCatalog</c>, beside
/// <see cref="ContentDegradationRegistry"/> — the registration whose container topology is proven
/// to reach the host-side health check), never static. Bounded by construction: one entry per
/// producing repository, and a repository that is released is removed.</para>
/// </summary>
public sealed class SealedSyncCensus
{
    /// <summary>The check's name on <c>/health</c>.</summary>
    public const string HealthCheckName = "publication-seal";

    /// <summary>
    /// 🚨 The ONE threshold, and it is DERIVED rather than chosen. The ordinary hold is the
    /// webhook/seal ordering: the green-build hook fires before the repository's publish-bake job
    /// seals the bundles for this identity, so a source waits out one publish job. Every CI job in
    /// this fleet is hard-cut at 45 minutes, so a hold that OUTLIVES that cap cannot be the
    /// ordinary race — the seal that would release it either landed (and the gate is reading a
    /// different identity) or was never made for this identity at all. That is the #4063 condition,
    /// and it is the only one this census escalates; every number prints whatever the status.
    /// </summary>
    public static readonly TimeSpan HoldIndictsAfter = TimeSpan.FromMinutes(45);

    // Instance state on a mesh-scoped singleton — never static, never cleared for test isolation:
    // one entry per producing repository currently held, removed the moment a delivery of that
    // repository is NOT held.
    private readonly ConcurrentDictionary<string, SealedSyncHold> held = new(StringComparer.OrdinalIgnoreCase);

    // Volatile reference, replaced whole. The newest reading always wins: unlike a gap high-water
    // mark this is a STATE, and a stale state is exactly what #4063 is about.
    private volatile SealedPublicationReading? published;

    /// <summary>
    /// Records what this identity has sealed. Called from the boot publication sweep and again from
    /// every green-build delivery (which reads the same index to decide), so the denominator stays
    /// as fresh as the deliveries are.
    /// </summary>
    /// <param name="reading">The reading.</param>
    public void RecordPublication(SealedPublicationReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        published = reading;
    }

    /// <summary>The most recent publication reading, or <c>null</c> when none was ever taken.</summary>
    public SealedPublicationReading? Published => published;

    /// <summary>
    /// Records that a repository's green build was held. 🚨 <see cref="SealedSyncHold.FirstObservedAt"/>
    /// is PRESERVED across deliveries: a freeze walks forward through head shas, and resetting the
    /// clock on each new commit would report a nine-hour freeze as a two-minute one.
    /// </summary>
    /// <param name="repository">The producing repository, <c>owner/name</c>.</param>
    /// <param name="builtCommit">The commit that was held.</param>
    /// <param name="reason">The gate's hold reason.</param>
    /// <param name="heldSources">How many sync sources this delivery held.</param>
    /// <param name="now">The clock.</param>
    public void RecordHold(string repository, string builtCommit, string reason, int heldSources, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return;
        held.AddOrUpdate(
            repository,
            _ => new SealedSyncHold(repository, builtCommit, reason, heldSources, now, now),
            (_, previous) => previous with
            {
                BuiltCommit = builtCommit,
                Reason = reason,
                HeldSources = heldSources,
                LastObservedAt = now,
            });
    }

    /// <summary>
    /// Records that a repository's green build was NOT held — the seal caught up, or the gate never
    /// applied. Removing the entry is what makes a released repository stop being reported; leaving
    /// it would turn the census into a log of everything that was ever held.
    /// </summary>
    /// <param name="repository">The producing repository, <c>owner/name</c>.</param>
    public void RecordRelease(string repository)
    {
        if (!string.IsNullOrWhiteSpace(repository))
            held.TryRemove(repository, out _);
    }

    /// <summary>Every repository currently held, longest-observed first.</summary>
    /// <returns>The holds.</returns>
    public ImmutableList<SealedSyncHold> Holds() =>
        held.Values
            .OrderBy(h => h.FirstObservedAt)
            .ThenBy(h => h.Repository, StringComparer.Ordinal)
            .ToImmutableList();

    /// <summary>
    /// 🚨 Whether a hold has outlived <see cref="HoldIndictsAfter"/>. An EMPTY set is not indicted,
    /// and neither is a hold that is minutes old — which is exactly why the sentence prints anyway.
    /// </summary>
    /// <param name="holds">The current holds.</param>
    /// <param name="now">The clock.</param>
    /// <returns><c>true</c> when some repository has been held past the CI job cap.</returns>
    public static bool IsIndicted(IReadOnlyList<SealedSyncHold> holds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(holds);
        return holds.Any(h => h.ObservedFor(now) >= HoldIndictsAfter);
    }

    /// <summary>
    /// The one sentence an operator reads on <c>/health</c>. Pure — it is the whole publication.
    ///
    /// <para>🚨 Three distinct absences, three distinct sentences: nothing measured at all; a
    /// deployment that consumes no CI bakes; and deliveries that arrived and were not held. Folding
    /// any two of them into one string would rebuild the ambiguity this census exists to remove.</para>
    /// </summary>
    /// <param name="published">The most recent publication reading, or <c>null</c>.</param>
    /// <param name="holds">Every repository currently held.</param>
    /// <param name="now">The clock.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(
        SealedPublicationReading? published, IReadOnlyList<SealedSyncHold> holds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(holds);
        if (published is null)
            return "NO publication-seal reading recorded on this replica — the boot publication "
                   + "sweep has not run here and no green build has been classified. There is "
                   + "nothing to read about GitSync holds here: this is an absence of measurement, "
                   + "NOT a clean one (MeshWeaver#4063).";

        var state = holds.Count == 0
            ? "no repository is currently held by the seal"
            : $"{holds.Count} repository/repositories HELD — {string.Join("; ", holds.Select(h => h.Line(now)))}";

        var verdict = IsIndicted(holds, now)
            ? $" 🚨 A hold has outlived the {HoldIndictsAfter.TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)}-minute "
              + "CI job cap, so it is NOT the ordinary webhook-before-seal ordering: this identity has no "
              + "publication of that repository at or after its last green build, and every GitSynced Space "
              + "of it is frozen until the instance is rolled onto the image the bake resolves (or the bake "
              + "resolves the image the instance runs)."
            : string.Empty;

        return $"{published.Line}; last read {published.At.ToString("O", CultureInfo.InvariantCulture)}. "
               + $"{state}.{verdict}";
    }
}

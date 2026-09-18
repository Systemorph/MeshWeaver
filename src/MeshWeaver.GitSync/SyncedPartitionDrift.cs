using System.Collections.Immutable;
using MeshWeaver.Hosting;
using MeshWeaver.Graph.Configuration;

namespace MeshWeaver.GitSync;

/// <summary>
/// Whether a partition's LIVE sources are the tree of the commit its sync source claims — measured
/// against the bundles this identity holds, without fetching anything.
///
/// <para>🚨 <b>Why this exists (MeshWeaver#4620).</b> The sealed GitSync import is a delta between
/// two COMMITS: it writes the files that moved between the previous sealed commit and the new one.
/// That is correct exactly while the mesh equals the previous commit's tree. Where anything else
/// wrote the partition since, the import leaves those files alone and the partition is left holding
/// one file from each tree — the mix <c>Doc/Architecture/OnePartitionOneBookkeeping</c> describes,
/// reached from the sync side.</para>
///
/// <para><b>Measured on memex.systemorph.com, 2026-09-17, twice over.</b> <c>Hosting</c> held
/// <c>Issue/Source/IssueLayoutAreas.cs</c> from the sealed commit <c>061976bc</c> beside
/// <c>Issue/Test/IssueTests.cs</c> from <c>main</c> (the boot install's copy, written 17:36:42Z);
/// the test calls <c>IssueLayoutAreas.Facts</c>/<c>StatusBadge</c>/<c>SeverityBadge</c>, which
/// <c>061976bc</c>'s view does not define, so <c>Hosting/Issue</c> sat at
/// <c>compilationStatus: Error</c> with three <c>CS0117</c>s for over a day. Its
/// <c>_GitSync</c> read <c>lastSyncOutcome: Imported</c>, <c>lastAttemptWasFinal: true</c>
/// throughout. <c>Crm</c> showed the same class from the other end: <c>lastSyncOutcome: Skipped</c>
/// — the content-skip short-circuit, which answers without reading the partition at all — beside
/// FOURTEEN types at <c>compilationStatus: Error</c>. Two of the instance's nineteen GitSync
/// partitions, both reporting success.</para>
///
/// <para>🚨 <b>The detector already existed; it was the INPUT that could not arrive.</b>
/// <see cref="SealedSyncReconcile.DecideWithInventory"/> has always answered
/// <see cref="SealedSyncReconcile.Action.ReconcileAtSealedCommit"/> for a source that claims the
/// sealed commit while types baked from it were declined on their source fingerprint — *"the live
/// sources have drifted from the commit they claim"*. Its evidence is the boot sweep's adoption
/// walk, and the ONE post-boot trigger — <see cref="PublicationSealArrivalService"/> — passed an
/// EMPTY declined set, so after boot the branch could only ever take the "nothing was declined"
/// exit. The detector reported a steady state having measured nothing. This type is that
/// measurement, taken where the inventory already is.</para>
///
/// <para><b>Why not the other two answers.</b> Always re-importing in full is what the git-diff was
/// introduced to stop (the memex-cloud outage loop of 2026-07-23) — and it would not even work: a
/// full import at an unchanged fingerprint hits the content-skip and returns "Skipped" without
/// reading the partition, which is exactly what <c>Crm</c> shows. Relying on one writer is refuted
/// by the same two measurements: the <c>Crm</c> mix involves no boot install at all.</para>
/// </summary>
public static class SyncedPartitionDrift
{
    /// <summary>
    /// A drift measurement, or the honest statement that none could be taken.
    ///
    /// <para>🚨 <b><see cref="Measured"/> is the whole point of this record.</b> "I looked and found
    /// no drift" and "I could not look" are the same empty list, and folding them is the defect this
    /// file exists to remove — one level up it is what made a publication announcement report a
    /// steady state it had never checked. A caller that cannot tell them apart must not conclude a
    /// steady state.</para>
    /// </summary>
    /// <param name="Measured">Whether a comparison was actually possible.</param>
    /// <param name="Drifted">The NodeType paths whose live sources no bundle for this identity
    /// records — empty AND <see cref="Measured"/> means the partition agrees with its commit.</param>
    /// <param name="Reason">Why, in one sentence an operator can act on.</param>
    /// <param name="Compared">How many types were actually compared — the DENOMINATOR of any claim
    /// made from <see cref="Drifted"/>, so "0 drifted" is never read without "of how many".</param>
    public sealed record Reading(
        bool Measured,
        ImmutableArray<string> Drifted,
        string Reason,
        int Compared = 0)
    {
        /// <summary>Nothing was compared and nothing is claimed.</summary>
        public static Reading NotMeasured(string reason) =>
            new(false, ImmutableArray<string>.Empty, reason);
    }

    /// <summary>
    /// The pure half: which of a partition's live NodeTypes hold sources no bundle for this identity
    /// records.
    ///
    /// <para>A type is COMPARED only when both sides can speak: its live definition states a
    /// <c>CurrentSourceFingerprint</c>, and some bundle NAMES it. A type no bundle names is not
    /// evidence of anything — it is a type this identity ships no bytes for — and a definition with
    /// no fingerprint has never been folded. Neither is counted as drift, and neither is counted in
    /// <see cref="Reading.Compared"/>.</para>
    ///
    /// <para>🚨 An UNUSABLE inventory abstains rather than reporting a clean partition: the shelf is
    /// the only thing that can say what the commit's tree should hash to, and "cannot tell" is never
    /// "clear". Same rule as <see cref="SealedPublicationSyncReconciler"/>'s own inventory read.</para>
    /// </summary>
    /// <param name="live">The partition's NodeType path → its live definition.</param>
    /// <param name="inventory">What bundles for this identity carry, per type.</param>
    /// <returns>The reading.</returns>
    public static Reading Measure(
        IReadOnlyDictionary<string, NodeTypeDefinition> live,
        PrebuiltBundleInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(inventory);

        if (!inventory.IsUsable)
            return Reading.NotMeasured(
                $"the bundle inventory for this identity is {inventory.Outcome} — nothing can be "
                + "compared against the commit's tree, so this partition is not judged");

        var drifted = ImmutableArray.CreateBuilder<string>();
        var compared = 0;
        foreach (var (path, definition) in live.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (definition.CurrentSourceFingerprint is not { Length: > 0 } fingerprint)
                continue;                       // never folded — nothing to compare
            if (!inventory.Names(path))
                continue;                       // this identity ships no bytes for it
            compared++;
            if (!inventory.Carries(path, fingerprint))
                drifted.Add(path);
        }

        if (compared == 0)
            return Reading.NotMeasured(
                $"no NodeType of this partition could be compared ({live.Count} live type(s), "
                + $"{inventory.Bundles} bundle archive(s) read) — the partition is not judged");

        return new Reading(
            true,
            drifted.ToImmutable(),
            drifted.Count == 0
                ? $"all {compared} comparable type(s) hold sources a bundle for this identity records"
                : $"{drifted.Count} of {compared} comparable type(s) hold sources NO bundle for this "
                  + $"identity records: {string.Join(", ", drifted.Take(5))}"
                  + (drifted.Count > 5 ? ", …" : ""),
            compared);
    }
}

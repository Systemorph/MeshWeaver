using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The incident: every new memex pod spent 8 min 24 s in the prebuilt-adoption pass before
/// readiness and adopted NOTHING (pod <c>…-5d998</c>, roll to 3.0.0-ci.10310, 2026-10-09:
/// <c>0 adopted now, 179 already current … in 00:08:24.62</c>). The timeline read from the pod's
/// own log split it: the cross-identity walk took its one source from the newest identity two
/// seconds in, then read the REST of the share silently for 7 min 44 s; all 71 bundles were then
/// judged in 38 s.
///
/// <para><b>The defect.</b> Under <c>Family</c> strictness every identity on the platform line is
/// admitted, and the walk read every source of every one of them IN FULL — pointer, seal, a probe
/// per listed bundle, two markers, the seal again, the probes again and every bundle's manifest (a
/// zip open) — before asking whether it still needed any source at all. On a shared network volume
/// each of those is a round-trip, so the pass grew with the share's history while its answer stayed
/// "nothing more to take".</para>
///
/// <para><b>What these tests pin</b>, on a share that COUNTS every operation (and prices each one at
/// what a round-trip costs on the Azure Files mount): the walk lists each identity once and reads
/// only the sources it still needs; the walk shape it replaced does an order of magnitude more on
/// the same fixture (the negative control — run, not asserted from memory); and the selection is
/// unchanged (the positive controls), so the fewer operations are not bought by taking less.</para>
/// </summary>
public class CrossIdentityWalkCostIsBoundedTest : IDisposable
{
    private const int Identities = 30;
    private const int Sources = 6;
    private const int BundlesPerSource = 10;
    private const string Needed = "meshweaver-content";

    /// <summary>A round-trip on the shared Azure Files volume, as the incident measured it.</summary>
    private static readonly TimeSpan RoundTrip = TimeSpan.FromSeconds(2);

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-walk-cost-" + Guid.NewGuid().ToString("N"));

    private static string IdentityName(int i) => $"s{i:D3}{new string('a', 29)}";

    private static IEnumerable<string> OtherSources() =>
        Enumerable.Range(0, Sources - 1).Select(i => $"source-{i}");

    /// <summary>
    /// A share counting every operation the walk asks of it. Nothing sleeps: the price is computed
    /// from the count, which is the quantity that grew.
    /// </summary>
    private sealed class CountingShare : BundleShareIo
    {
        private int operations;

        public int Operations => Volatile.Read(ref operations);

        public TimeSpan PricedAt(TimeSpan roundTrip) => roundTrip * Operations;

        private void Count() => Interlocked.Increment(ref operations);

        public override bool DirectoryExists(string path) { Count(); return base.DirectoryExists(path); }
        public override bool FileExists(string path) { Count(); return base.FileExists(path); }
        public override IReadOnlyList<string> EnumerateDirectories(string path) { Count(); return base.EnumerateDirectories(path); }
        public override string[] ReadAllLines(string path) { Count(); return base.ReadAllLines(path); }
        public override string ReadAllText(string path) { Count(); return base.ReadAllText(path); }
        public override Plugin.Packaging.BundleReader.Manifest? ReadManifest(string bundlePath)
        {
            Count();
            return base.ReadManifest(bundlePath);
        }
    }

    private void Publish(string identity, string source, bool sealedSource = true)
    {
        var dir = Path.Combine(root, identity, source);
        Directory.CreateDirectory(dir);
        var names = new List<string>();
        for (var b = 0; b < BundlesPerSource; b++)
        {
            var name = $"{source}-{b}.zip";
            using (ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create)) { }
            names.Add(name);
        }
        File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.RepositoryMarkerFileName), "Systemorph/" + source);
        File.WriteAllText(Path.Combine(dir, SealedPublicationIndex.SourceCommitMarkerFileName), "0123456789abcdef");
        if (sealedSource)
            File.WriteAllLines(Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName), names);
    }

    /// <summary>The memex shape: every admitted identity holds the same sources; the live one
    /// seals all of them but one, which the newest other identity supplies.</summary>
    private IReadOnlyList<(string Identity, string Version)> Share()
    {
        for (var i = 0; i < Identities; i++)
        {
            Publish(IdentityName(i), Needed);
            foreach (var source in OtherSources())
                Publish(IdentityName(i), source);
        }
        // Newest first, as the production wrapper orders them.
        return [.. Enumerable.Range(0, Identities).Select(i => (IdentityName(i), $"3.0.0-ci.{10000 - i}"))];
    }

    private ShippedPrebuiltBundles.FallbackWalk Walk(
        IReadOnlyList<(string Identity, string Version)> candidates, BundleShareIo io)
        => ShippedPrebuiltBundles.WalkFallbackIdentities(
            root, candidates, OtherSources().ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            "Family", runningPlatformVersion: null, logger: null, CancellationToken.None, io);

    /// <summary>The walk shape this replaced, composed from the same two reads it made per
    /// identity: an existence probe, every source's bundles, and every source's full reading.</summary>
    private int OldWalkOperations(IReadOnlyList<(string Identity, string Version)> candidates)
    {
        var share = new CountingShare();
        foreach (var (identity, _) in candidates)
        {
            var identityDir = Path.Combine(root, identity);
            if (!share.DirectoryExists(identityDir))
                continue;
            ShippedPrebuiltBundles.CompletePublishedBundlesOf(identityDir, logger: null, CancellationToken.None, io: share);
            SealedPublicationIndex.ReadAllSourcesFor(root, identity, logger: null, share);
        }
        return share.Operations;
    }

    [Fact]
    public void ANoOpWalk_ListsEachIdentityOnce_AndReadsOnlyTheSourceItNeeds()
    {
        var candidates = Share();
        var share = new CountingShare();

        var walk = Walk(candidates, share);

        walk.IdentitiesListed.Should().Be(Identities, "each identity on the share is listed exactly once");
        walk.SourcesRead.Should().Be(1,
            "only the one source the live identity does not seal is read, and only at the newest "
            + "identity that supplies it — every other identity's copy is already answered");
        // Root listing + one listing per identity + the one source read in full:
        // pointer (1) + seal (1) + a probe per bundle, then the reading: two markers + seal + a probe
        // and a manifest per bundle.
        var bound = 1 + Identities + (2 + BundlesPerSource) + (3 + 2 * BundlesPerSource);
        share.Operations.Should().BeLessThanOrEqualTo(bound,
            $"a no-op walk costs O(identities) listings, not O(identities × sources × bundles) reads "
            + $"(priced at {RoundTrip.TotalSeconds:0} s a round-trip: {share.PricedAt(RoundTrip)})");
    }

    [Fact]
    public void NegativeControl_TheWalkItReplaced_DoesAnOrderOfMagnitudeMore_OnTheSameShare()
    {
        var candidates = Share();
        var share = new CountingShare();
        Walk(candidates, share);

        var old = OldWalkOperations(candidates);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"share walk over {Identities} identities × {Sources} sources × {BundlesPerSource} bundles: "
            + $"new {share.Operations} ops ({share.PricedAt(RoundTrip)}), replaced {old} ops ({RoundTrip * old})");

        // Per identity it read every source twice over plus every manifest: the count grows with
        // identities × sources × bundles, which is what turned a share's history into boot time.
        old.Should().BeGreaterThanOrEqualTo(Identities * Sources * 3 * BundlesPerSource,
            "the replaced walk read every bundle of every source of every identity");
        old.Should().BeGreaterThan(share.Operations * 10,
            $"the old shape costs {old} round-trips ({RoundTrip * old}) against the new walk's "
            + $"{share.Operations} ({share.PricedAt(RoundTrip)}) — if this stops holding, the fixture no "
            + "longer exercises the defect and the bound above proves nothing");
    }

    [Fact]
    public void IdentitiesPrunedFromTheShare_CostNothing_BeyondTheOneRootListing()
    {
        var withoutAbsent = Share();
        var baseline = new CountingShare();
        Walk(withoutAbsent, baseline);

        var withAbsent = withoutAbsent
            .Concat(Enumerable.Range(0, 200).Select(i => ($"g{i:D3}gone", $"3.0.0-ci.{9000 - i}")))
            .ToList();
        var share = new CountingShare();
        Walk(withAbsent, share);

        share.Operations.Should().Be(baseline.Operations,
            "a candidate whose directory retention removed is answered by the root listing, never by "
            + "a probe of its own");
    }

    [Fact]
    public void PositiveControl_TheSelectionIsUnchanged_NewestSealedPublicationWins()
    {
        var candidates = Share();

        var walk = Walk(candidates, BundleShareIo.Real);

        walk.Bundles.Should().HaveCount(BundlesPerSource);
        walk.Bundles.Should().OnlyContain(b =>
                Path.GetDirectoryName(b) == Path.Combine(root, IdentityName(0), Needed),
            "the needed source comes from the NEWEST identity that seals it, and nothing the live "
            + "identity already seals is taken from anyone else");
    }

    [Fact]
    public void PositiveControl_AnUnsealedCopy_IsPassedOver_ForTheNextNewestSealedOne()
    {
        var candidates = Share();
        // The newest identity's copy is mid-republish (seal removed): the walk must read on.
        File.Delete(Path.Combine(root, IdentityName(0), Needed, ShippedPrebuiltBundles.CompletionSentinelFileName));

        var walk = Walk(candidates, BundleShareIo.Real);

        walk.SourcesRead.Should().Be(2, "the unsealed copy is read and passed over, then the next one is taken");
        walk.Bundles.Should().OnlyContain(b =>
            Path.GetDirectoryName(b) == Path.Combine(root, IdentityName(1), Needed));
        walk.Bundles.Should().HaveCount(BundlesPerSource);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

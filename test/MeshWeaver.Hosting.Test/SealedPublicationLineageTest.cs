using System;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>#3542 at its SECOND call site — the release markers, ordered by the version LABEL.</b>
///
/// <para>The self-updater was taught on 2026-09-07 to rank registry tags by the CD run number
/// rather than by SemVer, because a version LINE is a label a human maintains and a label can be
/// wrong: <c>3.1.0-ci.7832…7841</c> (the withdrawn 2026-09-05 slip) and <c>3.0.0-rc9.ci.7824</c>
/// (the retired rc line, where SemVer §11.4 compares the pre-release identifiers as TEXT so
/// <c>"ci"</c> &lt; <c>"rc9"</c>) both outrank the later, sealed <c>3.0.0-ci.8130</c> for ever.</para>
///
/// <para>The store reads the SAME labels one layer down: <c>_releases/&lt;platform-version&gt;</c>,
/// one marker per publication, named by exactly the string the tag carried. Three readers ranked
/// them by SemVer — <see cref="SealedPublicationIndex.ReleasesOf"/> (identity → its newest
/// version), <c>ShippedPrebuiltBundles.FallbackPublishedBundlesOf</c> (which sealed publication a
/// tolerant adoption takes) and <see cref="PrebuiltBundleStore.Plan"/> (which publications
/// survive the sweep) — so a mislabelled marker did not merely mis-rank a listing: it placed an
/// identity on a stale line, handed a three-day-old publication over as "newest", and PROTECTED
/// that publication from collection while the newest one was swept.</para>
///
/// <para>Every arm below is written against the incident's own data, and each asserts BOTH
/// directions: the mislabelled/retired marker loses, and a legitimately newer publication still
/// wins.</para>
/// </summary>
public class SealedPublicationLineageTest : IDisposable
{
    private const string Identity = "s0000000000000000000000000000one1";
    private const string OtherIdentity = "s0000000000000000000000000000two2";

    private readonly string root = Path.Combine(Path.GetTempPath(), "sealed-lineage-" + Guid.NewGuid().ToString("N"));

    private void Marker(string version, string identity)
    {
        var dir = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, version), identity + "\n");
    }

    // ───────── SealedPublicationIndex.ReleasesOf ─────────

    /// <summary>
    /// The withdrawn slip line. <c>3.1.0-ci.7841</c> is a 2026-09-05 build; <c>3.0.0-ci.8130</c> is
    /// later and sealed. By SemVer the slip wins for ever, so the identity would be reported as a
    /// 3.1.0 publication and every downstream reader would rank it first.
    /// </summary>
    [Fact]
    public void AMislabelledLine_IsNotTheIdentitysNewestPublication()
    {
        Marker("3.1.0-ci.7841", Identity);
        Marker("3.0.0-ci.8130", Identity);

        SealedPublicationIndex.ReleasesOf(root)[Identity].Should().Be("3.0.0-ci.8130",
            "run 8130 published after run 7841, whatever line each was labelled with");
    }

    /// <summary>
    /// The trap one layer down, and the reason deleting the 3.1.0 tags did not fix the incident:
    /// with them gone the selector simply moved to the next mislabelled marker.
    /// </summary>
    [Fact]
    public void ARetiredPrereleaseLabel_IsNotTheIdentitysNewestPublication()
    {
        Marker("3.0.0-rc9.ci.7824", Identity);
        Marker("3.0.0-ci.8130", Identity);

        SealedPublicationIndex.ReleasesOf(root)[Identity].Should().Be("3.0.0-ci.8130",
            "SemVer puts the text 'rc9' above 'ci', but run 8130 published three days after run 7824");
    }

    /// <summary>
    /// 🚨 The other direction, which the fix must not break: a genuinely later run still wins, and
    /// the marker order is independent of the order the directory happens to enumerate in.
    /// </summary>
    [Fact]
    public void ALaterRunStillWins_WhicheverOrderTheMarkersAreWritten()
    {
        Marker("3.0.0-ci.8130", Identity);
        Marker("3.0.0-ci.8059", Identity);
        Marker("3.0.0-ci.7845", Identity);
        SealedPublicationIndex.ReleasesOf(root)[Identity].Should().Be("3.0.0-ci.8130");

        Marker("3.0.0-ci.7845", OtherIdentity);
        Marker("3.0.0-ci.8200", OtherIdentity);
        SealedPublicationIndex.ReleasesOf(root)[OtherIdentity].Should().Be("3.0.0-ci.8200");
    }

    /// <summary>
    /// A PROMOTION carries no run number of its own (<c>release.yml</c> retags one already-sealed
    /// continuous set), so it is ranked behind every lineage-bearing marker and among the
    /// promotions by SemVer. Its identity is the same bytes the sibling ci marker names, so nothing
    /// is lost by it — and an identity named ONLY by promotions is still placed on its line.
    /// </summary>
    [Fact]
    public void APromotionIsRankedBehindTheLineageItWasCutFrom_ButStillPlacesAnIdentity()
    {
        Marker("3.0.0", Identity);
        Marker("3.0.0-ci.8130", Identity);
        SealedPublicationIndex.ReleasesOf(root)[Identity].Should().Be("3.0.0-ci.8130",
            "the clean release is a retag of a sealed continuous set — the run number is the publication");

        Marker("3.0.0", OtherIdentity);
        Marker("3.0.1", OtherIdentity);
        SealedPublicationIndex.ReleasesOf(root)[OtherIdentity].Should().Be("3.0.1",
            "among promotions the version string is the only key they share, and it is a trustworthy one");
    }

    // ───────── PrebuiltBundleRetention: the ordering decides what is DELETED ─────────

    /// <summary>
    /// 🚨 <b>The sweep is where a mis-ranking costs bytes rather than a listing.</b> With room for
    /// ONE publication per source, retention keeps the newest sealed one of the live line and
    /// collects the rest. Ranked by SemVer the retired <c>3.0.0-rc9.ci.7824</c> is "the newest" —
    /// so it would be protected and the later, sealed <c>3.0.0-ci.8130</c> publication, the one the
    /// running platform actually adopts, would be DELETED.
    /// </summary>
    [Fact]
    public void TheSweepKeepsTheNewestRun_NotTheHighestLabel()
    {
        var plan = Sweep(
            new ReleaseMarkerEntry("3.0.0-rc9.ci.7824", Identity, "/m/3.0.0-rc9.ci.7824", At(1), null),
            new ReleaseMarkerEntry("3.0.0-ci.8130", OtherIdentity, "/m/3.0.0-ci.8130", At(2), null));

        plan.AbortReason.Should().BeNull();
        plan.Protected.Should().ContainKey(OtherIdentity,
            "run 8130 is the newest sealed 'plugins' publication — the one the running platform adopts");
        // …and the retired rc9 publication, which only outranks it as a LABEL (SemVer §11.4
        // compares 'rc9' and 'ci' as text), is the one collected.
        plan.Collectable.Select(i => i.Identity).Should().Equal(Identity);
    }

    /// <summary>The other direction: a genuinely newer publication is the one kept.</summary>
    [Fact]
    public void TheSweepStillKeepsAGenuinelyNewerPublication()
    {
        var plan = Sweep(
            new ReleaseMarkerEntry("3.0.0-ci.8200", Identity, "/m/3.0.0-ci.8200", At(1), null),
            new ReleaseMarkerEntry("3.0.0-ci.8130", OtherIdentity, "/m/3.0.0-ci.8130", At(2), null));

        plan.AbortReason.Should().BeNull();
        plan.Protected.Should().ContainKey(Identity, "run 8200 published after run 8130");
        plan.Collectable.Select(i => i.Identity).Should().Equal(OtherIdentity);
    }

    /// <summary>Two sealed 'plugins' publications, room for exactly one — so the ORDER decides which
    /// survives and which is deleted. The live identity is neither of them.</summary>
    private static PrebuiltBundleSweepPlan Sweep(params ReleaseMarkerEntry[] markers) =>
        PrebuiltBundleStore.Plan(
            liveIdentity: "s00000000000000000000000000live1",
            livePlatformVersion: "3.0.0-ci.8130",
            scan: new PrebuiltStoreScan(
                [.. markers.Select(m => SealedIdentity(m.Identity!, "plugins", m.WrittenUtc))],
                [.. markers]),
            stampedIdentities: [],
            // Nothing is pinned here on purpose: this test isolates the LINEAGE order, so retention
            // must decide on the seal times alone. A pin would protect an identity for a second,
            // unrelated reason and make the ordering assertion below unfalsifiable.
            pinned: [],
            retention: new PrebuiltBundleRetention { KeepNewestPerSource = 1 },
            nowUtc: At(10));

    private static DateTimeOffset At(int hours) =>
        new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero).AddHours(hours);

    private static PrebuiltIdentityEntry SealedIdentity(string identity, string source, DateTimeOffset sealedAt) =>
        new(identity, "/store/" + identity,
            [new PrebuiltSourceEntry(source, true, sealedAt, sealedAt, null)],
            Bytes: 1024, NewestWriteUtc: sealedAt);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}

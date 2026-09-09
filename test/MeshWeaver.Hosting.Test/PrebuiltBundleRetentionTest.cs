using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The prebuilt-bundle retention sweep — the rule "prune what nothing references, never by age
/// alone" over a temp store tree. One test per KEEP rule proves the rule keeps; the deleting
/// tests prove the rest goes; and the fail-closed cases (an unreadable seal keeps its identity, an
/// unreadable release marker aborts the pass) pin the direction a wrong answer must fall in.
/// </summary>
public class PrebuiltBundleRetentionTest : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);
    private const string Live = "s-live";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-prebuilt-{Guid.NewGuid():N}");
    private readonly PrebuiltBundleRetention retention = PrebuiltBundleRetention.Default with { KeepNewestPerSource = 2 };

    public PrebuiltBundleRetentionTest() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ---- fixture helpers ----------------------------------------------------------------------

    private string Sealed(string identity, string source, DateTimeOffset sealedAt, int bytes = 1024)
    {
        var dir = Path.Combine(root, identity, source);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "A.zip"), new byte[bytes]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "A.zip"), sealedAt.UtcDateTime);
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        File.WriteAllText(sentinel, "A.zip\n");
        File.SetLastWriteTimeUtc(sentinel, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(dir, sealedAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, identity), sealedAt.UtcDateTime);
        return dir;
    }

    private string Unsealed(string identity, string source, DateTimeOffset writtenAt)
    {
        var dir = Path.Combine(root, identity, source);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "A.zip"), new byte[64]);
        File.SetLastWriteTimeUtc(Path.Combine(dir, "A.zip"), writtenAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(dir, writtenAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, identity), writtenAt.UtcDateTime);
        return dir;
    }

    private string Marker(string version, string identity, DateTimeOffset? writtenAt = null)
    {
        var dir = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, version);
        File.WriteAllText(path, identity + "\n");
        File.SetLastWriteTimeUtc(path, (writtenAt ?? Now - TimeSpan.FromDays(100)).UtcDateTime);
        return path;
    }

    private static DateTimeOffset DaysAgo(int days) => Now - TimeSpan.FromDays(days);

    private PrebuiltBundleSweepPlan PlanNow(
        string? liveVersion = "3.1.0-ci.9000", ImmutableHashSet<string>? stamps = null,
        ImmutableList<PinnedPlatformReference>? pinned = null) =>
        PrebuiltBundleStore.Plan(Live, liveVersion, PrebuiltBundleStore.Scan(root), stamps ?? [], pinned ?? [], retention, Now);

    private static string[] Ids(PrebuiltBundleSweepPlan plan) => plan.Collectable.Select(i => i.Identity).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    // ---- the keep rules -----------------------------------------------------------------------

    [Fact]
    public void TheRunningIdentity_IsKept_HoweverOld()
    {
        Sealed(Live, "plugins", DaysAgo(400));
        Sealed("s-old", "plugins", DaysAgo(400));
        // Only the running-identity rule keeps the old live publication.
        Sealed("s-u1", "plugins", DaysAgo(2));
        Sealed("s-u2", "plugins", DaysAgo(3));

        var plan = PlanNow();

        plan.Protected.Keys.Should().Contain(Live);
        plan.Protected[Live].Should().Contain("this process runs");
        Ids(plan).Should().Contain("s-old");
        Ids(plan).Should().NotContain(Live);
    }

    [Fact]
    public void AnIdentityNamedByACleanReleaseMarker_IsKept_AndItsCiSiblingsOfThatLineGo()
    {
        Marker("3.1.0-ci.9000", Live);
        Marker("3.0.0", "s-release"); Sealed("s-release", "plugins", DaysAgo(300));
        Marker("3.0.0-ci.10", "s-ci10"); Sealed("s-ci10", "plugins", DaysAgo(320));
        Marker("3.0.0-ci.20", "s-ci20"); Sealed("s-ci20", "plugins", DaysAgo(310));
        Marker("3.0.0-ci.30", "s-ci30"); Sealed("s-ci30", "plugins", DaysAgo(305));
        Sealed(Live, "plugins", DaysAgo(1));

        // The running line is 4.x, so no 3.x identity is what Family adopts here.
        var plan = PlanNow(liveVersion: "4.0.0-ci.1");

        plan.Protected[ "s-release"].Should().Contain("release marker 3.0.0");
        // Line 3.0.0 is CLOSED by the clean marker: every -ci identity of it is unreferenced.
        Ids(plan).Should().Equal("s-ci10", "s-ci20", "s-ci30");
        // …and their markers retire with them; the clean marker never does.
        plan.CollectableMarkers.Select(m => m.Version).OrderBy(v => v, StringComparer.Ordinal).Should().Equal("3.0.0-ci.10", "3.0.0-ci.20", "3.0.0-ci.30");
    }

    [Fact]
    public void EachMajor_KeepsItsLatestSealedPublicationPerSource_AndRecentArtifactsWithoutACountQuota()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        // ci.900 sorts above ci.3758 as TEXT; by version it is the oldest.
        Marker("3.1.0-ci.900", "s-900"); Sealed("s-900", "plugins", DaysAgo(30));
        Marker("3.1.0-ci.3758", "s-3758"); Sealed("s-3758", "plugins", DaysAgo(20));
        Marker("3.1.0-ci.4000", "s-4000"); Sealed("s-4000", "plugins", DaysAgo(10));
        // Preserve each source's latest sealed publication even beyond 30 days.
        Marker("3.1.0-ci.50", "s-edu50"); Sealed("s-edu50", "education", DaysAgo(40));

        var plan = PlanNow(liveVersion: "4.0.0-ci.1");

        plan.Protected.Keys.Should().Contain("s-3758");
        plan.Protected.Keys.Should().Contain("s-4000");
        plan.Protected.Keys.Should().Contain("s-edu50");
        Ids(plan).Should().Equal("s-900");
        plan.CollectableMarkers.Select(m => m.Version).OrderBy(v => v, StringComparer.Ordinal).Should().Equal("3.1.0-ci.900");
    }

    [Fact]
    public void TheNewestSealedPublicationOnTheRunningLine_IsKept_BecauseFamilyStrictnessAdoptsIt()
    {
        // The older education publication is still its source's latest on this major.
        Marker("3.0.0", "s-rel"); Sealed("s-rel", "plugins", DaysAgo(200));
        Marker("3.0.0-ci.5", "s-fam"); Sealed("s-fam", "education", DaysAgo(150));
        Marker("3.2.0-ci.1", "s-a"); Sealed("s-a", "plugins", DaysAgo(3));
        Marker("3.2.0-ci.2", "s-b"); Sealed("s-b", "plugins", DaysAgo(2));
        Sealed(Live, "plugins", DaysAgo(1));

        var plan = PlanNow(liveVersion: "3.1.0-ci.9000");

        plan.Protected[ "s-fam"].Should().Contain("Family strictness");
        Ids(plan).Should().BeEmpty();
    }

    [Fact]
    public void AnIdentityANodeTypeRecordIsStampedWith_IsKept()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-stamped", "plugins", DaysAgo(500));
        Sealed("s-forgotten", "plugins", DaysAgo(600));
        // Only the adoption stamp keeps the old stamped publication.
        Sealed("s-recent", "plugins", DaysAgo(2));

        var plan = PlanNow(stamps: ["s-stamped"]);

        plan.Protected[ "s-stamped"].Should().Contain("CompiledFrameworkVersion");
        Ids(plan).Should().Equal("s-forgotten");
    }

    [Fact]
    public void UnnamedIdentities_KeepThirtyDays_RegardlessOfNewerBuildCount()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-u1", "plugins", DaysAgo(30));
        Sealed("s-u2", "plugins", DaysAgo(20));
        Sealed("s-u3", "plugins", DaysAgo(10));

        var plan = PlanNow();

        // Only the artifact at the 30-day boundary is eligible; newer count is irrelevant.
        plan.Protected[ "s-u3"].Should().Contain("30-day retention window");
        plan.Protected.Keys.Should().Contain("s-u2");
        Ids(plan).Should().Equal("s-u1");
    }

    [Fact]
    public void AnUnsealedDirectoryYoungerThanTheGrace_IsASealInFlight_AndKept()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Unsealed("s-inflight", "plugins", Now - TimeSpan.FromMinutes(5));
        Unsealed("s-dead", "plugins", DaysAgo(40));

        var plan = PlanNow();

        plan.Protected[ "s-inflight"].Should().Contain("30-day retention window");
        Ids(plan).Should().Equal("s-dead");
    }

    /// <summary>
    /// 🚨 The registry case: pearl is pinned to 3.0.0-ci.8080, a closed line and far outside the
    /// newest ten, and pulls exactly that identity's seal over the HTTP prebuilt surface. The
    /// Deployment record's pin reaches the identity through the _releases marker, and that keeps it
    /// — and its marker — whatever the line and recency rules say.
    /// </summary>
    [Fact]
    public void AnIdentityADeploymentRecordPins_IsKept_ViaItsReleaseMarker_RegardlessOfLineAndRecency()
    {
        Marker("3.1.0-ci.9000", Live);
        Sealed(Live, "plugins", DaysAgo(1));
        Marker("3.0.0", "s-rel"); Sealed("s-rel", "plugins", DaysAgo(100));
        Marker("3.0.0-ci.8080", "s-8080"); Sealed("s-8080", "plugins", DaysAgo(120));
        Marker("3.0.0-ci.8081", "s-8081"); Sealed("s-8081", "plugins", DaysAgo(110));
        Marker("3.1.0-ci.1", "s-a"); Sealed("s-a", "plugins", DaysAgo(3));
        Marker("3.1.0-ci.2", "s-b"); Sealed("s-b", "plugins", DaysAgo(2));

        // The pin is written as an IMAGE TAG, the way a Deployment record carries it.
        var plan = PlanNow(liveVersion: "4.0.0-ci.1",
            pinned: [new PinnedPlatformReference("Deployment Deployments/pearl", "memex-portal-ai:3.0.0-ci.8080", null)]);

        plan.Protected["s-8080"].Should().Contain("pinned by Deployment Deployments/pearl at 3.0.0-ci.8080");
        Ids(plan).Should().Equal("s-8081");
        plan.CollectableMarkers.Select(m => m.Version).Should().Equal("3.0.0-ci.8081");
        plan.UnresolvedPins.Should().BeEmpty();
    }

    [Fact]
    public void AnIdentityARegisteredInstanceReports_IsKeptDirectly_WithoutAMarker()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-remote", "plugins", DaysAgo(400));
        Sealed("s-old", "plugins", DaysAgo(400));
        Sealed("s-u1", "plugins", DaysAgo(2));

        var plan = PlanNow(pinned: [new PinnedPlatformReference("instance report Deployments/Modules/atioz", "3.0.0-ci.7000+abc", "s-remote")]);

        plan.Protected["s-remote"].Should().Contain("pinned by instance report");
        plan.Protected["s-remote"].Should().Contain("3.0.0-ci.7000");
        Ids(plan).Should().Equal("s-old");
    }

    [Fact]
    public void AConsumerVersionNoMarkerNames_AbortsCleanup_AndTheLedgerSaysSo()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-old", "plugins", DaysAgo(500));
        Sealed("s-recent", "plugins", DaysAgo(2));

        var pinned = ImmutableList.Create(new PinnedPlatformReference("Deployment Deployments/ghost", "3.0.0-ci.1", null));
        var plan = PlanNow(pinned: pinned);

        plan.UnresolvedPins.Should().Equal("Deployment Deployments/ghost=3.0.0-ci.1");
        plan.AbortReason.Should().Contain("inventory is incomplete");
        Ids(plan).Should().BeEmpty();

        var result = PrebuiltBundleStore.SweepCore(root, Live, "3.1.0-ci.9000", [], pinned, retention with { Delete = false }, Now);
        File.ReadAllText(PrebuiltBundleStore.LedgerPathOf(root))
            .Should().Contain("unresolved consumer references (cleanup blocked): Deployment Deployments/ghost=3.0.0-ci.1");
        result.Plan!.UnresolvedPins.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("memex-portal-ai:3.0.0-ci.8080", "3.0.0-ci.8080")]
    [InlineData("meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.8080@sha256:abc", "3.0.0-ci.8080")]
    [InlineData("3.1.0+deadbeef", "3.1.0")]
    [InlineData("  ", null)]
    public void PlatformVersionLine_ReducesAPinToTheMarkerName(string pin, string? expected)
    {
        PlatformVersionLine.Normalize(pin).Should().Be(expected);
    }

    // ---- fail closed --------------------------------------------------------------------------

    [Fact]
    public void ASealThatCannotBeRead_KeepsItsIdentity()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        var dir = Sealed("s-unreadable", "plugins", DaysAgo(500));
        // The seam the scan actually has is File.ReadAllLines throwing: hold the sentinel open with
        // FileShare.None for the scan's duration (an advisory lock on Unix, a sharing violation on Windows).
        var sentinel = Path.Combine(dir, ShippedPrebuiltBundles.CompletionSentinelFileName);
        using var hold = new FileStream(sentinel, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = PlanNow();

        plan.Protected.Keys.Should().Contain("s-unreadable");
        plan.Protected["s-unreadable"].Should().Contain("could not be read");
        Ids(plan).Should().BeEmpty();
    }

    [Fact]
    public void AReleaseMarkerThatCannotBeRead_AbortsTheWholePass()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-old", "plugins", DaysAgo(500));
        var marker = Marker("3.0.0", "s-old");
        using var hold = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = PlanNow();

        plan.AbortReason.Should().Contain("3.0.0");
        Ids(plan).Should().BeEmpty();
        plan.CollectableMarkers.Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyMarker_IsUnreadableToo()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-old", "plugins", DaysAgo(500));
        File.WriteAllText(Marker("3.0.0", "ignored"), "");

        PlanNow().AbortReason.Should().Contain("empty");
    }

    // ---- the deleting half --------------------------------------------------------------------

    [Fact]
    public void SweepCore_RemovesOnlyTheCollectable_OldestFirst_AndRetiresTheirMarkers()
    {
        Marker("3.1.0-ci.9000", Live);
        Sealed(Live, "plugins", DaysAgo(1));
        Marker("3.0.0", "s-rel"); Sealed("s-rel", "plugins", DaysAgo(200));
        Marker("3.0.0-ci.1", "s-c1"); Sealed("s-c1", "plugins", DaysAgo(230), bytes: 4096);
        Marker("3.0.0-ci.2", "s-c2"); Sealed("s-c2", "plugins", DaysAgo(220), bytes: 2048);
        Unsealed("s-dead", "plugins", DaysAgo(40));
        // A -ci marker whose identity is long gone retires as well — _releases must not grow forever.
        Marker("3.0.0-ci.0", "s-gone");
        // …but a young dangling marker may precede its bundles (the publisher writes it per run).
        Marker("3.0.0-ci.3", "s-coming", writtenAt: Now - TimeSpan.FromMinutes(1));

        var removed = new System.Collections.Generic.List<string>();
        var result = PrebuiltBundleStore.SweepCore(root, Live, "4.0.0-ci.1", [], [], retention, Now,
            deleteDirectory: dir => { removed.Add(Path.GetFileName(dir)); Directory.Delete(dir, true); });

        result.Deleted.Should().BeTrue();
        result.AbortReason.Should().BeNull();
        removed.Should().Equal("s-c1", "s-c2", "s-dead");
        result.DeletedIdentities.Should().Be(3);
        result.DeletedBytes.Should().BeGreaterThan(4096 + 2048);
        Directory.Exists(Path.Combine(root, Live)).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "s-rel")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "s-c1")).Should().BeFalse();

        var markers = Directory.GetFiles(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName))
            .Select(Path.GetFileName).OrderBy(n => n).ToArray();
        markers.Should().Equal("3.0.0", "3.0.0-ci.3", "3.1.0-ci.9000");
        result.DeletedMarkers.Should().Be(3);

        var ledger = File.ReadAllLines(PrebuiltBundleStore.LedgerPathOf(root));
        ledger[0].Should().Contain("removed 3 identity(ies)");
        ledger.Should().Contain(l => l.Contains("removed s-c1"));
        ledger.Should().Contain(l => l.Contains("retired marker 3.0.0-ci.0"));
    }

    [Fact]
    public void SweepCore_ReportOnly_RemovesNothing()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-old", "plugins", DaysAgo(500));
        Sealed("s-recent", "plugins", DaysAgo(2));

        var result = PrebuiltBundleStore.SweepCore(root, Live, "3.1.0-ci.9000", [], [], retention with { Delete = false }, Now);

        result.Deleted.Should().BeFalse();
        result.Plan!.Collectable.Select(i => i.Identity).Should().Equal("s-old");
        Directory.Exists(Path.Combine(root, "s-old")).Should().BeTrue();
        File.ReadAllText(PrebuiltBundleStore.LedgerPathOf(root)).Should().Contain("report only");
    }

    [Fact]
    public void SweepCore_AFailedRemoval_IsCountedAndTheRestProceeds()
    {
        Sealed(Live, "plugins", DaysAgo(1));
        Sealed("s-a", "plugins", DaysAgo(500));
        Sealed("s-b", "plugins", DaysAgo(400));
        Sealed("s-recent", "plugins", DaysAgo(2));

        var result = PrebuiltBundleStore.SweepCore(root, Live, "3.1.0-ci.9000", [], [], retention, Now,
            deleteDirectory: dir =>
            {
                if (dir.EndsWith("s-a", StringComparison.Ordinal))
                    throw new IOException("locked");
                Directory.Delete(dir, true);
            });

        result.FailedDeletes.Should().Be(1);
        result.DeletedIdentities.Should().Be(1);
        Directory.Exists(Path.Combine(root, "s-b")).Should().BeFalse();
    }

    [Fact]
    public void SweepCore_AnAbsentRoot_CollectsNothing()
    {
        var result = PrebuiltBundleStore.SweepCore(Path.Combine(root, "missing"), Live, null, [], [], retention, Now);
        result.Plan.Should().BeNull();
        result.AbortReason.Should().Contain("does not exist");
    }

    [Fact]
    public void MoreThanTenNewerBuilds_CannotCollectARecentBundle()
    {
        Sealed("s-recent", "plugins", DaysAgo(29));
        for (var i = 0; i < 20; i++)
            Sealed($"s-new-{i}", "plugins", DaysAgo(1));
        Sealed("s-expired", "plugins", DaysAgo(31));

        Ids(PlanNow()).Should().Equal("s-expired");
    }

    [Fact]
    public void ARecentPublicationMarker_ProtectsOlderBytes()
    {
        Sealed("s-republished", "plugins", DaysAgo(100));
        Marker("3.0.0-ci.1", "s-republished", DaysAgo(1));
        Sealed("s-newer", "plugins", DaysAgo(100));
        Marker("3.0.0-ci.2", "s-newer");

        PlanNow().Protected.Keys.Should().Contain("s-republished");
    }

    [Fact]
    public void AReportedFallback_SurvivesANewerSameMajorPublication()
    {
        Sealed("s-fallback", "plugins", DaysAgo(100));
        Marker("3.0.0-ci.1", "s-fallback");
        Sealed("s-newer", "plugins", DaysAgo(90));
        Marker("3.0.0-ci.2", "s-newer");

        var plan = PlanNow(pinned: [new PinnedPlatformReference("adopted fallback", null, "s-fallback")]);

        plan.Protected.Keys.Should().Contain("s-fallback");
        plan.Protected.Keys.Should().Contain("s-newer");
        Ids(plan).Should().BeEmpty();
    }

    [Fact]
    public void AnUnsealedSuccessor_DoesNotDisplaceTheLastSealedPublication()
    {
        Sealed("s-green", "plugins", DaysAgo(100));
        Marker("3.0.0-ci.1", "s-green");
        Unsealed("s-failed", "plugins", DaysAgo(50));
        Marker("3.0.0-ci.2", "s-failed");

        var plan = PlanNow(liveVersion: "4.0.0-ci.1");

        plan.Protected.Keys.Should().Contain("s-green");
        Ids(plan).Should().Equal("s-failed");
    }

    // ---- version lines ------------------------------------------------------------------------

    [Theory]
    [InlineData("3.1.0-ci.8076", "3.1.0", true)]
    [InlineData("3.1.0", "3.1.0", false)]
    [InlineData("3.1.0+abc123", "3.1.0", false)]
    [InlineData("3.0.0-rc9", "3.0.0", true)]
    [InlineData("garbage", null, false)]
    public void PlatformVersionLine_PlacesAVersionOnItsLine(string version, string? line, bool prerelease)
    {
        PlatformVersionLine.LineOf(version).Should().Be(line);
        PlatformVersionLine.IsPrerelease(version).Should().Be(prerelease);
    }

    [Theory]
    [InlineData("00:00:00", 30)]
    [InlineData("1.00:00:00", 30)]
    [InlineData("30.00:00:00", 30)]
    [InlineData("60.00:00:00", 60)]
    public void Configuration_CanExtendButCannotShortenTheThirtyDayWindow(string age, int days)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                [PrebuiltBundleRetentionExtensions.MinimumAgeConfigKey] = age,
                [PrebuiltBundleRetentionExtensions.KeepNewestPerSourceConfigKey] = "0",
            }).Build();

        var configured = PrebuiltBundleRetentionExtensions.FromConfiguration(configuration);
        configured.MinimumAge.Should().Be(TimeSpan.FromDays(days));
        Sealed("s-recent", "plugins", DaysAgo(29));
        PrebuiltBundleStore.Plan(Live, null, PrebuiltBundleStore.Scan(root), [], [], configured, Now)
            .Collectable.Should().BeEmpty();
    }

    [Fact]
    public void FromConfiguration_DegradesEveryMalformedKnobToItsDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                [PrebuiltBundleRetentionExtensions.DeleteConfigKey] = "false",
                [PrebuiltBundleRetentionExtensions.KeepNewestPerSourceConfigKey] = "ten",
                [PrebuiltBundleRetentionExtensions.IntervalConfigKey] = "06:00:00",
            })
            .Build();

        var read = PrebuiltBundleRetentionExtensions.FromConfiguration(configuration);

        read.Delete.Should().BeFalse();
        read.KeepNewestPerSource.Should().Be(10);
        read.Interval.Should().Be(TimeSpan.FromHours(6));
        read.UnsealedGrace.Should().Be(PrebuiltBundleRetention.Default.UnsealedGrace);
    }
}

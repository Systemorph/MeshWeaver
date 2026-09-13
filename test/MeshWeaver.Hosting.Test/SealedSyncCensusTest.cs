#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The census that makes MeshWeaver#4063 readable, pinned at the semantics an operator's
/// conclusion rests on.</b> A publication seal that stops advancing freezes every GitSynced Space
/// of that repository, and on 2026-09-12 it did so on both production portals for nine hours while
/// every per-node field read <c>Imported</c> / attempted == synced / <c>lastAttemptWasFinal:
/// true</c>. The reading that separates a freeze from a quiet week is a DURATION, so the two things
/// that could quietly destroy it are the two things asserted hardest here: a hold clock that
/// restarts, and a release that never happens.
/// </summary>
public class SealedSyncCensusTest
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 21, 55, 0, TimeSpan.Zero);

    /// <summary>
    /// 🚨 The defect a naive implementation has and no assertion would otherwise catch: a freeze
    /// walks FORWARD through head shas — eight green builds in the #4063 window — so recording each
    /// delivery as a fresh hold would report a nine-hour freeze as a two-minute one, permanently
    /// under the threshold and permanently Healthy.
    /// </summary>
    [Fact]
    public void AFreezeWalkingThroughHeadShas_KeepsTheFirstObservation()
    {
        var census = new SealedSyncCensus();
        census.RecordHold("Systemorph/MeshWeaver.Plugins", "7660ca73", "not sealed (a)", 34, T0);
        census.RecordHold("Systemorph/MeshWeaver.Plugins", "222853d4", "not sealed (b)", 34, T0.AddHours(2));
        census.RecordHold("Systemorph/MeshWeaver.Plugins", "4b97be19", "not sealed (c)", 34, T0.AddHours(9));

        var holds = census.Holds();
        holds.Should().ContainSingle("one repository held is ONE entry, however many builds it held");
        holds[0].FirstObservedAt.Should().Be(T0,
            "the hold clock must survive a new head sha — a freeze advances through commits, and "
            + "restarting the clock on each one reports nine hours as the gap between the last two "
            + "builds, which is under every threshold by construction");
        holds[0].BuiltCommit.Should().Be("4b97be19", "the newest held build is the one to report");
        holds[0].Reason.Should().Be("not sealed (c)", "so is its reason");
        holds[0].LastObservedAt.Should().Be(T0.AddHours(9));
        holds[0].ObservedFor(T0.AddHours(9)).Should().Be(TimeSpan.FromHours(9));
    }

    /// <summary>
    /// The other half: once the seal catches up the repository must STOP being reported, or the
    /// census degrades into a log of everything that was ever held and its Degraded verdict becomes
    /// permanent noise.
    /// </summary>
    [Fact]
    public void ARelease_RemovesTheRepository_AndTheNextHoldStartsANewClock()
    {
        var census = new SealedSyncCensus();
        census.RecordHold("Systemorph/MeshWeaver.Plugins", "7660ca73", "not sealed", 34, T0);
        census.RecordRelease("Systemorph/MeshWeaver.Plugins");

        census.Holds().Should().BeEmpty("the seal caught up; nothing is frozen");

        census.RecordHold("Systemorph/MeshWeaver.Plugins", "4b97be19", "not sealed again", 34, T0.AddHours(9));
        census.Holds().Should().ContainSingle().Which.FirstObservedAt.Should().Be(T0.AddHours(9),
            "a hold that follows a RELEASE is a new freeze and must be timed from its own start, "
            + "not from a previous one the seal already resolved");
    }

    /// <summary>
    /// 🚨 The threshold is DERIVED, not chosen: the ordinary hold is the webhook firing before the
    /// repository's publish-bake seals, and every CI job in this fleet is hard-cut at 45 minutes.
    /// A hold inside that window is the design working; past it, the ordering cannot explain it.
    /// </summary>
    [Theory]
    [InlineData(44, false)]
    [InlineData(45, true)]
    [InlineData(540, true)]
    public void OnlyAHoldPastTheCiJobCap_IsIndicted(int minutesHeld, bool indicted)
    {
        var census = new SealedSyncCensus();
        census.RecordHold("Systemorph/MeshWeaver.Plugins", "7660ca73", "not sealed", 34, T0);

        SealedSyncCensus.IsIndicted(census.Holds(), T0.AddMinutes(minutesHeld)).Should().Be(indicted,
            $"a hold of {minutesHeld} minute(s) against a {SealedSyncCensus.HoldIndictsAfter.TotalMinutes}-minute "
            + "job cap; degrading inside the cap would make every portal non-Healthy on every "
            + "satellite merge, and not degrading past it is #4063 going unreported");
    }

    /// <summary>
    /// 🚨 Three absences, three sentences. Folding any two together rebuilds the ambiguity the
    /// census exists to remove — "I did not measure", "there is nothing here to measure" and "I
    /// measured, and it was clean" are different facts about a portal.
    /// </summary>
    [Fact]
    public void TheThreeAbsences_AreThreeDifferentSentences()
    {
        var unmeasured = SealedSyncCensus.Describe(null, [], T0);
        var noBakes = SealedSyncCensus.Describe(
            new SealedPublicationReading("s6649734", null, [], T0), [], T0);
        var clean = SealedSyncCensus.Describe(
            new SealedPublicationReading("s6649734", "/data/prebuilt-bundles",
                [new SealedSource("plugins", "Systemorph/MeshWeaver.Plugins", "4b97be19", true, null)],
                T0),
            [], T0);

        unmeasured.Should().Contain("absence of measurement, NOT a clean one");
        noBakes.Should().Contain("no published bundle root is configured");
        clean.Should().Contain("no repository is currently held by the seal");

        new[] { unmeasured, noBakes, clean }.Distinct(StringComparer.Ordinal).Should().HaveCount(3,
            "two absences that print the same sentence are one absence to every reader of /health");
        clean.Should().Contain("'plugins' ← Systemorph/MeshWeaver.Plugins @ 4b97be19 (sealed)",
            "the clean reading must carry the DENOMINATOR — which publications this identity holds "
            + "— or 'none held' is a number without a population");
    }

    /// <summary>
    /// A seal that is present but TORN is not a clean denominator either, and the census must say
    /// which and why — that publication is exactly the one that cannot release a source.
    /// </summary>
    [Fact]
    public void ATornSeal_IsReportedAsNotSealed_WithItsRefusal()
    {
        var reading = new SealedPublicationReading("s6649734", "/data/prebuilt-bundles",
            ImmutableList.Create(
                new SealedSource("plugins", "Systemorph/MeshWeaver.Plugins", "4b97be19", false,
                    "the seal lists 'MeshWeaver.AI.zip', which is not on disk")),
            T0);

        SealedSyncCensus.Describe(reading, [], T0).Should()
            .Contain("(NOT sealed: the seal lists 'MeshWeaver.AI.zip', which is not on disk)",
                "a torn publication reads as 'sealed' to anyone who only counts directories, and it "
                + "is precisely the publication that will hold every source of its repository");
    }
}

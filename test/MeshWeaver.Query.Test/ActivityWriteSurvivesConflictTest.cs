using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// 🚨 A cross-hub write refused as STALE must converge on the retry — issue #1910.
///
/// <para><b>The incident.</b> <c>ActivityTracking</c> failed to record a navigation:
/// <c>MeshNode Conflict at 'rbuergi/_UserActivity/rbuergi': cross-hub write refused: 3 field(s)
/// changed on the owner since the writer's base and nothing was applied — re-read and
/// re-apply</c>. The activity entry for that moment simply does not exist. The issue proposed
/// "adding a retry-with-refresh loop, or switching to an append-only pattern".</para>
///
/// <para><b>What was actually there, and why it could not work.</b> A retry-with-refresh loop
/// already existed. <c>UpdateRemote</c> re-enqueues on <c>Conflict</c> (and re-runs the update
/// lambda rather than re-posting the patch — the #1814 defect, already fixed). What it did NOT do
/// is guarantee that the re-run sees anything DIFFERENT. It rebuilt from <c>mirror.Take(1)</c> —
/// whatever this hub's mirror holds at that instant — and immediately after a NACK that is very
/// often still the version the owner just refused. The lambda then recomputes the same values
/// from the same base, <c>ExtractBaseValues</c> emits the same base, and the owner refuses
/// identically. Three attempts, three identical patches, then a terminal
/// <c>MeshNodeStreamException</c> and the write is gone. <b>A retry whose input cannot change is
/// not a retry</b> — the same shape as #1814 one level down: there the patch was re-posted, here
/// the base is re-read from a source that had not moved.</para>
///
/// <para><b>Why waiting is sound rather than hopeful.</b> A <c>Conflict</c> is the owner stating
/// that it is AHEAD of the writer's base — it minted a version for the write that beat us BEFORE
/// it answered us. So the state that makes the re-attempt converge already exists and is on its
/// way to this mirror. The re-attempt waits for that specific fact (a version strictly greater
/// than the one refused), bounded, and falls back to the old behaviour if it never arrives —
/// which is why the change can only improve on what was there.</para>
///
/// <para>Deterministic: the mirror and the clock are both seams. No hub, no cluster, no
/// wall-clock sleep — the interleaving that makes this defect rare in a test and common in
/// production is CONSTRUCTED here rather than raced for.</para>
/// </summary>
public class ActivityWriteSurvivesConflictTest
{
    /// <summary>The activity node from the incident.</summary>
    private const string Path = "rbuergi/_UserActivity/rbuergi";

    /// <summary>
    /// A stand-in for this hub's mirror: replays its current state to a new subscriber and emits
    /// again as the owner's commits arrive — the shape <c>UpdateRemote</c> reads.
    /// </summary>
    private static ReplaySubject<MeshNode> Mirror(long version)
    {
        var mirror = new ReplaySubject<MeshNode>(1);
        mirror.OnNext(NodeAt(version));
        return mirror;
    }

    private static MeshNode NodeAt(long version) =>
        MeshNode.FromPath(Path) with { NodeType = "UserActivity", Version = version };

    /// <summary>
    /// 🚨 THE regression pin. The owner refused version 10 as stale; the re-attempt must not
    /// rebuild from version 10.
    ///
    /// <para>Against <c>origin/main</c> this fails immediately with <c>10</c> — see
    /// <see cref="NegativeControl_TheOldShapeRebuildsFromTheVeryVersionTheOwnerRefused"/>, which
    /// is that shape written out.</para>
    /// </summary>
    [Fact]
    public void A_conflict_retry_rebases_on_state_newer_than_the_version_that_was_refused()
    {
        var scheduler = new TestScheduler();
        var mirror = Mirror(version: 10);
        var stale = new List<long>();
        var seen = new List<long>();

        MeshNodeStreamHandle
            .RebaseSource(mirror, refusedBaseVersion: 10, stale.Add, scheduler)
            .Subscribe(node => seen.Add(node.Version));

        // The mirror still holds exactly what the owner refused. Rebuilding from it would produce
        // the same patch with the same base — refused again, and again, until the budget is spent
        // and the caller's write is dropped.
        seen.Should().BeEmpty(
            "re-running the lambda against the very version the owner refused recomputes the same "
            + "patch from the same base — the retry cannot converge, which is how a tracked "
            + "activity ends up silently absent");

        // The winning writer's commit arrives at the mirror — it was already in flight, because
        // the owner committed it before it NACK'd us.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
        mirror.OnNext(NodeAt(11));

        seen.Should().Equal([11L],
            "the re-attempt rebases on the state the owner actually has, which IS the 're-read and "
            + "re-apply' the refusal asked for");
        stale.Should().BeEmpty("the mirror advanced, so nothing degraded");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the shape <c>UpdateRemote</c> had, written out. It is one operator, and
    /// it is the whole defect: a re-attempt is handed the refused version and rebuilds from it.
    /// Keeping it here means the regression above cannot quietly become vacuous.
    /// </summary>
    [Fact]
    public void NegativeControl_TheOldShapeRebuildsFromTheVeryVersionTheOwnerRefused()
    {
        var mirror = Mirror(version: 10);
        var seen = new List<long>();

        // origin/main: `remoteStream.Where(c => c.Value is not null).Take(1)`.
        mirror.Take(1).Subscribe(node => seen.Add(node.Version));

        seen.Should().Equal([10L],
            "this is the defect: the retry reads the mirror as it stands, which right after a "
            + "Conflict NACK is still the version the owner refused — so the recomputed patch is "
            + "byte-identical to the one that was just rejected");
    }

    /// <summary>
    /// The bound, and the reason it is a bound and not a wait: a mirror that never advances must
    /// not park the caller. The re-attempt proceeds against what it has — exactly the previous
    /// behaviour — and REPORTS that it did, so "refused as stale AND the mirror never caught up"
    /// is a fact in the log rather than a second silent refusal.
    /// </summary>
    [Fact]
    public void A_mirror_that_never_advances_is_not_parked_on_and_the_degradation_is_reported()
    {
        var scheduler = new TestScheduler();
        var mirror = Mirror(version: 10);
        var stale = new List<long>();
        var seen = new List<long>();

        MeshNodeStreamHandle
            .RebaseSource(mirror, refusedBaseVersion: 10, stale.Add, scheduler)
            .Subscribe(node => seen.Add(node.Version));

        scheduler.AdvanceBy(TimeSpan.FromSeconds(4).Ticks);
        seen.Should().BeEmpty("still inside the bound");

        scheduler.AdvanceBy(TimeSpan.FromSeconds(2).Ticks);

        seen.Should().Equal([10L],
            "a fresher base never arrived, so the re-attempt does the best it can rather than "
            + "hanging — never worse than the behaviour it replaces");
        stale.Should().Equal([10L],
            "…and says so, because a re-attempt that is about to be refused for the same reason "
            + "is exactly the thing that was invisible");
    }

    /// <summary>
    /// A FIRST attempt has no refused base and must not wait for anything — the fix costs the
    /// normal write path nothing. This is the case every cross-hub write in the mesh takes.
    /// </summary>
    [Fact]
    public void A_first_attempt_reads_the_mirror_immediately()
    {
        var scheduler = new TestScheduler();
        var seen = new List<long>();

        MeshNodeStreamHandle
            .RebaseSource(Mirror(version: 10), refusedBaseVersion: 0, _ => { }, scheduler)
            .Subscribe(node => seen.Add(node.Version));

        seen.Should().Equal([10L],
            "no timer, no gate — the ordinary write path is untouched, which is what makes this "
            + "safe to put on every cross-hub Update");
    }

    /// <summary>
    /// 🚨 The hazard the FILTER introduces, closed in the same change. Waiting for a newer version
    /// creates a completion the un-filtered shape could not produce: a mirror that ENDS (its hub
    /// torn down) while holding only the refused version would complete this source with no value
    /// at all — the write's observer never fires and the caller waits on a pipeline that has
    /// already finished. A source that cannot answer must say so; an empty completion is the one
    /// outcome a caller cannot distinguish from "still working".
    /// </summary>
    [Fact]
    public void A_mirror_that_ends_without_newer_state_errors_rather_than_completing_empty()
    {
        var scheduler = new TestScheduler();
        var mirror = Mirror(version: 10);
        var seen = new List<long>();
        Exception? error = null;
        var completed = false;

        MeshNodeStreamHandle
            .RebaseSource(mirror, refusedBaseVersion: 10, _ => { }, scheduler)
            .Subscribe(node => seen.Add(node.Version), ex => error = ex, () => completed = true);

        // The owning hub goes away before anything newer arrives.
        mirror.OnCompleted();

        seen.Should().BeEmpty();
        completed.Should().BeFalse(
            "an empty completion is indistinguishable from 'still working' to the caller — it is "
            + "how a write turns into a hang instead of a failure");
        error.Should().NotBeNull(
            "the re-attempt has nothing to rebase on and must terminate the caller, not strand it");
        error!.Message.Should().Contain("did NOT land",
            "…and say plainly that the write is gone, so the caller can re-issue it");
    }

    /// <summary>
    /// 🚨 The distinction that keeps the bound from being burned for nothing: only
    /// <c>Conflict</c> means "the owner moved past you". <c>OwnerDisposing</c> /
    /// <c>OwnerNotReady</c> mean the patch never reached a merge at all — no newer version was
    /// minted, so there is nothing to wait for. Those re-enqueues pass <c>0</c> and behave exactly
    /// as they did.
    /// </summary>
    [Fact]
    public void A_non_staleness_reenqueue_does_not_wait_for_a_version_that_will_never_come()
    {
        var scheduler = new TestScheduler();
        var seen = new List<long>();

        // What an OwnerDisposing / OwnerNotReady re-enqueue passes.
        MeshNodeStreamHandle
            .RebaseSource(Mirror(version: 10), refusedBaseVersion: 0, _ => { }, scheduler)
            .Subscribe(node => seen.Add(node.Version));

        seen.Should().Equal([10L],
            "a patch that never reached a merge left the owner's version untouched — waiting for "
            + "it to advance would spend the whole bound before every one of these retries");
    }
}

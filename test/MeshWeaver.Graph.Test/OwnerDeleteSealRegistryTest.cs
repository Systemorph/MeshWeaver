using System;
using System.Reactive.Disposables;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The owner's delete seal in <see cref="RecentlyDeletedRegistry"/> (#6856), in isolation: an ABORT
/// that reaches the owner before its version check leaves a mark, and the check that runs afterwards
/// refuses to take the seal. Without the mark that late seal had no release left to receive and the
/// owner's writes to the path stayed deferred until the activation was torn down.
/// </summary>
public class OwnerDeleteSealRegistryTest
{
    private const string Path = "Space/Doc";

    /// <summary>A stand-in for <c>RegisterForDisposalDetachable</c>: collects what the hub's teardown would dispose.</summary>
    private sealed class FakeLifetime
    {
        public CompositeDisposable OnTeardown { get; } = new();

        public IDisposable Bind(IDisposable registrant)
        {
            OnTeardown.Add(registrant);
            return Disposable.Create(() => OnTeardown.Remove(registrant));
        }
    }

    [Fact(Timeout = 5000)]
    public void AnAbortThatOvertakesTheVersionCheck_MakesTheCheckRefuseTheSeal()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();

        registry.ReleaseOwnerDeleteSeal("seal-1", Path, DeleteSealOutcome.Aborted, owner.Bind)
            .Should().BeFalse("no seal was held yet: the abort overtook the check");
        registry.TakeOwnerDeleteSeal("seal-1", Path, owner.Bind)
            .Should().BeFalse("the delete already gave up; a seal taken now would never be released");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse("nothing seals the path");
        registry.IsUnderActiveDeletion(Path, out _).Should().BeFalse("no row guard was left behind");
        owner.OnTeardown.Count.Should().Be(0, "the consumed mark detached itself from the owner's lifetime");
    }

    [Fact(Timeout = 5000)]
    public void WithoutAnEarlyAbort_TheSealIsTaken_AndTheAbortReleasesIt()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();

        registry.TakeOwnerDeleteSeal("seal-2", Path, owner.Bind).Should().BeTrue("control: nothing aborted it");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue();
        registry.IsUnderActiveDeletion(Path, out _).Should().BeTrue("the owner's row guard is held");
        registry.ReleaseOwnerDeleteSeal("seal-2", Path, DeleteSealOutcome.Aborted, owner.Bind)
            .Should().BeTrue("the abort releases the held seal");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse();
        registry.IsUnderActiveDeletion(Path, out _).Should().BeFalse();
        owner.OnTeardown.Count.Should().Be(0, "the released seal detached from the owner's lifetime");
    }

    [Fact(Timeout = 5000)]
    public void AnAbortMark_LivesOnlyAsLongAsTheOwnerActivation()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var first = new FakeLifetime();
        registry.ReleaseOwnerDeleteSeal("seal-3", Path, DeleteSealOutcome.Aborted, first.Bind);

        first.OnTeardown.Dispose(); // the activation that could have run the check is gone

        registry.TakeOwnerDeleteSeal("seal-3", Path, new FakeLifetime().Bind)
            .Should().BeTrue("the mark went with its activation; nothing of it outlives the owner");
    }

    [Fact(Timeout = 5000)]
    public void AReleaseFromAnotherProcessHub_LeavesNoMark()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        registry.ReleaseOwnerDeleteSeal("seal-4", Path, DeleteSealOutcome.Committed).Should().BeFalse();
        registry.TakeOwnerDeleteSeal("seal-4", Path, new FakeLifetime().Bind)
            .Should().BeTrue("only the owner's own hub records an early abort");
    }

    [Fact(Timeout = 5000)]
    public void AReleaseNamingAnUnknownOutcome_LeavesTheSealInPlace()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        registry.TakeOwnerDeleteSeal("seal-5", Path, owner.Bind).Should().BeTrue();

        registry.ReleaseOwnerDeleteSeal("seal-5", Path, "CommittedV2", owner.Bind)
            .Should().BeFalse("an outcome this process does not know must not silently mean 'aborted'");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue("the deferred writes stay deferred");
        registry.IsUnderActiveDeletion(Path, out _).Should().BeTrue("the row guard stays");

        registry.ReleaseOwnerDeleteSeal("seal-5", Path, DeleteSealOutcome.Committed).Should().BeTrue(
            "control: a known outcome still releases it");
    }

    [Fact(Timeout = 5000)]
    public void ATeardownWithAFence_HoldsTheVersionGuardUntilTheFenceTerminates()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        var fence = new System.Reactive.Subjects.AsyncSubject<bool>();
        var fenced = 0;
        registry.TakeOwnerDeleteSeal("seal-6", Path, owner.Bind, _ =>
        {
            fenced++;
            return fence;
        }).Should().BeTrue();

        owner.OnTeardown.Dispose();

        fenced.Should().Be(1, "the owner's teardown runs the store fence");
        registry.IsUnderActiveDeletion(Path, out _).Should().BeTrue(
            "the row guard STAYS while the fence runs: only the fence's own write passes it");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue(
            "until the fence lands, a replacement here defers its writes");
        fence.OnNext(false);
        fence.OnCompleted();
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse("the fence landed");
        registry.IsRecentlyDeleted(Path).Should().BeFalse("the row survived, so nothing is tombstoned");
    }

    [Fact(Timeout = 5000)]
    public void AFenceThatFindsTheRowGone_TombstonesThePath()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        registry.TakeOwnerDeleteSeal("seal-7", Path, owner.Bind, _ => System.Reactive.Linq.Observable.Return(true));
        owner.OnTeardown.Dispose();
        registry.IsRecentlyDeleted(Path).Should().BeTrue("the delete won the race; nothing deferred may store the node again");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse();
    }

    [Fact(Timeout = 5000)]
    public void AFenceThatFaults_KeepsThePathSealed_UntilTheDeletesVerdict()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        var runs = 0;
        registry.TakeOwnerDeleteSeal("seal-8", Path, owner.Bind, _ =>
        {
            runs++;
            return System.Reactive.Linq.Observable.Throw<bool>(new InvalidOperationException("store unavailable"));
        });
        owner.OnTeardown.Dispose();

        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue(
            "a failed fence proves nothing about the row: a replacement must not acknowledge a write yet");
        registry.RetryFailedFence(Path);
        runs.Should().Be(2, "a deferred write runs the fence again");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue();

        registry.ReleaseOwnerDeleteSeal("seal-8", Path, DeleteSealOutcome.Aborted).Should().BeTrue(
            "the delete's own verdict still reaches the entry");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse("the abort settles it");
        registry.IsRecentlyDeleted(Path).Should().BeFalse();
    }

    [Fact(Timeout = 5000)]
    public void ACommitDuringTheFence_StopsIt_TombstonesThePath_AndIsAnsweredOnlyOnceSettled()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        var fence = new System.Reactive.Subjects.AsyncSubject<bool>();
        Func<bool>? stop = null;
        registry.TakeOwnerDeleteSeal("seal-9", Path, owner.Bind, s =>
        {
            stop = s;
            return fence;
        });
        owner.OnTeardown.Dispose();

        registry.ReleaseOwnerDeleteSeal("seal-9", Path, DeleteSealOutcome.Committed).Should().BeTrue();
        stop!().Should().BeTrue("the fence stands down before its next write");
        registry.IsRecentlyDeleted(Path).Should().BeTrue("the node is gone; nothing deferred stores it again");
        var settled = false;
        using var _ = registry.OwnerSealSettled("seal-9").Subscribe(__ => settled = true);
        settled.Should().BeFalse("the commit is answered only once the in-flight fence has terminated");
        registry.TakeOwnerDeleteSeal("seal-9", Path, new FakeLifetime().Bind).Should().BeFalse(
            "a re-ask while fencing takes no fresh seal");

        fence.OnNext(false);
        fence.OnCompleted();
        settled.Should().BeTrue();
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse();
    }

    /// <summary>
    /// Review round 5: a commit release and the owner's teardown RACE. Before, the commit could read
    /// "not fencing", the teardown could then begin fencing, and the commit still removed the entry —
    /// so the running fence never heard the delete's verdict (and a late "row gone" could tombstone a
    /// path the caller had already recreated). Now claiming the held seal and beginning to fence are
    /// one atomic choice: whichever wins, a fence that runs always receives the verdict. Raced on two
    /// threads, many times; each round must end in one of the two consistent states.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void ACommitRacingTheOwnersTeardown_AlwaysReachesARunningFence()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 3000; round++)
        {
            ct.ThrowIfCancellationRequested();
            var registry = new RecentlyDeletedRegistry();
            var owner = new FakeLifetime();
            var fence = new System.Reactive.Subjects.AsyncSubject<bool>();
            Func<bool>? stop = null;
            registry.TakeOwnerDeleteSeal($"race-{round}", Path, owner.Bind, s =>
            {
                stop = s;
                return fence;
            }).Should().BeTrue();

            var go = 0;
            var committer = new System.Threading.Thread(() =>
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref go) == 1);
                registry.ReleaseOwnerDeleteSeal($"race-{round}", Path, DeleteSealOutcome.Committed);
            });
            var teardown = new System.Threading.Thread(() =>
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref go) == 1);
                owner.OnTeardown.Dispose();
            });
            committer.Start();
            teardown.Start();
            System.Threading.Volatile.Write(ref go, 1);
            committer.Join();
            teardown.Join();

            if (stop is not null)
                stop().Should().BeTrue(
                    $"round {round}: the teardown's fence ran, so the commit must have reached it — not removed the entry from under it");
            registry.IsRecentlyDeleted(Path).Should().BeTrue($"round {round}: the commit tombstones the path either way");
            fence.OnNext(false);
            fence.OnCompleted();
            registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse($"round {round}: everything settled");
        }
    }

    /// <summary>
    /// Review round 6: the fence reports the row GONE and settles while the delete's commit arrives.
    /// Before, settling removed the entry first and tombstoned the path after, so a commit landing in
    /// between found no seal and was acknowledged at once — its caller could recreate the path, and the
    /// late tombstone then dropped the recreated node's saves. Now the entry stays discoverable while it
    /// settles and the commit's acknowledgement waits for it: at the moment the commit is acknowledged,
    /// the path is ALREADY tombstoned. Raced on two threads, many times.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void ACommitArrivingWhileTheFenceSettles_IsAcknowledgedOnlyAfterTheTombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 5000; round++)
        {
            ct.ThrowIfCancellationRequested();
            var registry = new RecentlyDeletedRegistry();
            var owner = new FakeLifetime();
            var fence = new System.Reactive.Subjects.AsyncSubject<bool>();
            var sealId = $"settle-{round}";
            registry.TakeOwnerDeleteSeal(sealId, Path, owner.Bind, _ => fence).Should().BeTrue();
            owner.OnTeardown.Dispose(); // fencing now

            var go = 0;
            var tombstonedAtAck = (bool?)null;
            var fenceSide = new System.Threading.Thread(() =>
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref go) == 1);
                fence.OnNext(true); // the row is gone: the delete won
                fence.OnCompleted();
            });
            var commitSide = new System.Threading.Thread(() =>
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref go) == 1);
                registry.ReleaseOwnerDeleteSeal(sealId, Path, DeleteSealOutcome.Committed);
                // What the release handler does before it answers the deleter.
                System.Reactive.Linq.Observable.Take(registry.OwnerSealSettled(sealId), 1)
                    .Subscribe(_ => tombstonedAtAck = registry.IsRecentlyDeleted(Path));
            });
            fenceSide.Start();
            commitSide.Start();
            System.Threading.Volatile.Write(ref go, 1);
            fenceSide.Join();
            commitSide.Join();

            tombstonedAtAck.Should().BeTrue(
                $"round {round}: the commit is acknowledged only once the settling fence has tombstoned the path");
            registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse($"round {round}: settled");
        }
    }

    /// <summary>
    /// Review round 7 (4306349698): a FAILED fence attempt must not settle the retry that replaced it.
    /// Each attempt's completion is a lane command naming its attempt; the seal settles only when the
    /// CURRENT attempt finishes — a commit that arrives while the retry runs waits for that retry.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void AFailedAttempt_NeverSettlesTheRetryThatReplacedIt()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        var attempts = new System.Collections.Generic.List<System.Reactive.Subjects.AsyncSubject<bool>>();
        registry.TakeOwnerDeleteSeal("attempts", Path, owner.Bind, _ =>
        {
            var attempt = new System.Reactive.Subjects.AsyncSubject<bool>();
            attempts.Add(attempt);
            return attempt;
        }).Should().BeTrue();
        owner.OnTeardown.Dispose();                                // attempt 1 runs
        attempts[0].OnError(new InvalidOperationException("store unavailable"));
        registry.RetryFailedFence(Path);                           // attempt 2 runs
        attempts.Should().HaveCount(2);

        registry.ReleaseOwnerDeleteSeal("attempts", Path, DeleteSealOutcome.Committed).Should().BeTrue();
        var settled = false;
        using var _ = registry.OwnerSealSettled("attempts").Subscribe(__ => settled = true);
        settled.Should().BeFalse("attempt 2 can still write: the commit waits for it, not for attempt 1's fault");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue();

        attempts[1].OnNext(false);
        attempts[1].OnCompleted();
        settled.Should().BeTrue();
        registry.IsRecentlyDeleted(Path).Should().BeTrue();
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse();
    }

    /// <summary>
    /// Review round 7 (4306349715): a commit racing the fence's completion must never expose the path
    /// UNSEALED before it is tombstoned. The commit records its verdict and the tombstone in ONE lane
    /// command; settling is another. Raced on two threads: at the moment the path unseals for a
    /// committed delete, the tombstone is already there.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void ACommitRacingTheFencesCompletion_NeverUnsealsThePathBeforeItsTombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < 3000; round++)
        {
            ct.ThrowIfCancellationRequested();
            var registry = new RecentlyDeletedRegistry();
            var owner = new FakeLifetime();
            var fence = new System.Reactive.Subjects.AsyncSubject<bool>();
            var sealId = $"unseal-{round}";
            registry.TakeOwnerDeleteSeal(sealId, Path, owner.Bind, _ => fence).Should().BeTrue();
            owner.OnTeardown.Dispose();
            string? unsealedFor = null;
            var tombstonedWhenUnsealed = false;
            using var watch = System.Reactive.Linq.Observable.Where(
                    registry.VersionGuardReleases(Path), o => o != DeleteSealOutcome.OwnerTeardown)
                .Subscribe(o =>
                {
                    unsealedFor = o;
                    tombstonedWhenUnsealed = registry.IsRecentlyDeleted(Path);
                });

            var go = 0;
            var commitReached = false;
            var commitSide = new System.Threading.Thread(() =>
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref go) == 1);
                commitReached = registry.ReleaseOwnerDeleteSeal(sealId, Path, DeleteSealOutcome.Committed);
            });
            var fenceSide = new System.Threading.Thread(() =>
            {
                System.Threading.SpinWait.SpinUntil(() => System.Threading.Volatile.Read(ref go) == 1);
                fence.OnNext(false); // the fence stood down (or fenced) — not "row gone"
                fence.OnCompleted();
            });
            commitSide.Start();
            fenceSide.Start();
            System.Threading.Volatile.Write(ref go, 1);
            commitSide.Join();
            fenceSide.Join();

            unsealedFor.Should().NotBeNull($"round {round}: the seal settled");
            if (commitReached)
            {
                unsealedFor.Should().Be(DeleteSealOutcome.Committed,
                    $"round {round}: the commit reached the seal, so the path unseals as committed");
                tombstonedWhenUnsealed.Should().BeTrue(
                    $"round {round}: a committed delete is tombstoned before the path is unsealed");
            }
        }
    }

    /// <summary>
    /// Review round 7 (4306349658): an INDETERMINATE outcome (the delete gave up while its storage delete
    /// may still be running) is no abort. The owner runs the store fence and stays sealed until it
    /// lands; only then do its deferred writes run.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void AnIndeterminateOutcome_FencesBeforeTheSealLifts()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        var fence = new System.Reactive.Subjects.AsyncSubject<bool>();
        var ran = 0;
        registry.TakeOwnerDeleteSeal("indeterminate", Path, owner.Bind, _ =>
        {
            ran++;
            return fence;
        }).Should().BeTrue();

        registry.ReleaseOwnerDeleteSeal("indeterminate", Path, DeleteSealOutcome.Indeterminate).Should().BeTrue();
        ran.Should().Be(1, "the fence decides an indeterminate delete");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue("not an abort: deferred writes wait for the fence");
        fence.OnNext(false);
        fence.OnCompleted();
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse("the fence moved the row; the delete can no longer remove it");
        registry.IsRecentlyDeleted(Path).Should().BeFalse();
    }

    /// <summary>
    /// Review round 8 (4306680127, 4306680138): the release asks what the STORE answered, as the mesh
    /// observed it — not how far the delete's pipeline got. The storage call is subscribed by the
    /// registry and runs to its real termination even after the pipeline unsubscribed.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void TheStoresAnswer_DecidesTheVerdict_EvenAfterThePipelineUnsubscribed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();

        // Removed, but the pipeline is cut BEFORE it records progress: still Committed.
        var removed = new System.Reactive.Subjects.AsyncSubject<bool>();
        registry.OpenRootDeletes(Path);
        registry.ObserveRootDelete(Path, removed).Subscribe(_ => { }, _ => { }).Dispose();
        removed.OnNext(true);
        removed.OnCompleted();
        registry.TakeRootDeleteVerdict(Path).Should().Be(RootDeleteVerdict.Removed,
            "the store removed the row; the pipeline never got to record it, and that must not read as an abort");

        // Unanswered when the release asks: Indeterminate; the late answer leaves nothing behind.
        var running = new System.Reactive.Subjects.AsyncSubject<bool>();
        registry.OpenRootDeletes(Path);
        registry.ObserveRootDelete(Path, running).Subscribe(_ => { }, _ => { }).Dispose();
        registry.TakeRootDeleteVerdict(Path).Should().Be(RootDeleteVerdict.Unanswered);
        running.OnNext(false);
        running.OnCompleted();
        registry.TakeRootDeleteVerdict(Path).Should().Be(RootDeleteVerdict.NotRemoved,
            "a late answer is not retained to misclassify a later delete of the same path");

        // Refused: an abort.
        registry.OpenRootDeletes(Path);
        registry.ObserveRootDelete(Path, System.Reactive.Linq.Observable.Return(false)).Subscribe(_ => { }, _ => { });
        registry.TakeRootDeleteVerdict(Path).Should().Be(RootDeleteVerdict.NotRemoved);
    }

    /// <summary>
    /// Review round 9 (4306761565): a FAILED storage call proves nothing about the row (a provider can
    /// remove a copy before another faults). Its verdict stays Unanswered — the delete is Indeterminate
    /// and the owner fences — never NotRemoved, which would send an abort and resume writes.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void AFailedStorageCall_IsIndeterminate_NeverAnAbort()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        registry.OpenRootDeletes("delete-a");
        registry.ObserveRootDelete("delete-a",
                System.Reactive.Linq.Observable.Throw<bool>(new InvalidOperationException("one provider faulted")))
            .Subscribe(_ => { }, _ => { });
        registry.TakeRootDeleteVerdict("delete-a").Should().Be(RootDeleteVerdict.Unanswered);
    }

    /// <summary>
    /// Review round 9 (4306761580): root-delete records belong to ONE delete, keyed by its seal id. A
    /// concurrent delete of the same path that releases first must not consume — or be answered by —
    /// another delete's held storage call.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void OverlappingDeletesOfOnePath_NeverConsumeEachOthersVerdict()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var heldA = new System.Reactive.Subjects.AsyncSubject<bool>();
        registry.OpenRootDeletes("delete-a");
        registry.OpenRootDeletes("delete-b");
        registry.ObserveRootDelete("delete-a", heldA).Subscribe(_ => { }, _ => { });

        registry.TakeRootDeleteVerdict("delete-b").Should().Be(RootDeleteVerdict.NotRemoved,
            "delete B issued no root delete of its own");
        registry.TakeRootDeleteVerdict("delete-a").Should().Be(RootDeleteVerdict.Unanswered,
            "A's held storage call is still A's — B's release did not consume it");
        heldA.OnNext(true);
        heldA.OnCompleted();
    }

    /// <summary>
    /// Review round 10 (4306865446): a root delete that tries to ISSUE after its delete's release
    /// decided the outcome is refused before it reaches the store. The per-delete record is opened
    /// before any work and closed by the release in the same step that reads it.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void ARootDeleteIssuedAfterTheReleaseDecided_NeverReachesTheStore()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        registry.OpenRootDeletes("late");
        registry.TakeRootDeleteVerdict("late").Should().Be(RootDeleteVerdict.NotRemoved,
            "nothing was issued yet: the release decides Aborted and closes the record");

        var subscribed = 0;
        var late = System.Reactive.Linq.Observable.Defer(() =>
        {
            subscribed++;
            return System.Reactive.Linq.Observable.Return(true);
        });
        Exception? refused = null;
        registry.ObserveRootDelete("late", late).Subscribe(_ => { }, ex => refused = ex);
        subscribed.Should().Be(0, "the storage delete is never issued once the outcome was decided");
        refused.Should().NotBeNull("the late caller hears why");
    }

    /// <summary>
    /// Review round 10 (4306865461): a removal in an earlier drain pass does not settle a LATER root
    /// delete that is still running. An unanswered call dominates, and the release is Indeterminate —
    /// even when the pipeline recorded the root as removed.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void AnUnansweredLaterPass_DominatesAnEarlierRemoval()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        registry.OpenRootDeletes("passes");
        registry.ObserveRootDelete("passes", System.Reactive.Linq.Observable.Return(true)).Subscribe(_ => { }, _ => { });
        var secondPass = new System.Reactive.Subjects.AsyncSubject<bool>();
        registry.ObserveRootDelete("passes", secondPass).Subscribe(_ => { }, _ => { });

        var verdict = registry.TakeRootDeleteVerdict("passes");
        verdict.Should().Be(RootDeleteVerdict.Unanswered, "the second pass's delete is still running");
        MeshExtensions.SealOutcomeFor(rootRemoved: true, verdict).Should().Be(DeleteSealOutcome.Indeterminate,
            "a recorded removal must not lift the owner's seal while a later root delete can still remove a recreated node");
        MeshExtensions.SealOutcomeFor(rootRemoved: true, RootDeleteVerdict.NotRemoved).Should().Be(DeleteSealOutcome.Committed);
        MeshExtensions.SealOutcomeFor(rootRemoved: false, RootDeleteVerdict.NotRemoved).Should().Be(DeleteSealOutcome.Aborted);
        secondPass.OnNext(false);
        secondPass.OnCompleted();
    }

    /// <summary>
    /// Review thread 4307955118: a version-guarded delete and an overlapping UNVERSIONED delete both
    /// guard the path; the seal aborts first (the subtree scope still holds), then the other delete
    /// aborts. The END of the last guard — here a subtree scope, which used to announce nothing — must
    /// signal, so a save either guard refused is posted.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void TheLastGuardToEnd_SignalsTheRefusedSave_WhicheverKindItIs()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var signals = 0;
        using var watch = registry.DeletionGuardsLifted(Path).Subscribe(_ => signals++);

        var subtree = registry.BeginSubtreeDeletion(Path);           // the unversioned delete
        registry.TakeOwnerDeleteSeal("overlap", Path, new FakeLifetime().Bind).Should().BeTrue();

        registry.ReleaseOwnerDeleteSeal("overlap", Path, DeleteSealOutcome.Aborted).Should().BeTrue();
        signals.Should().Be(0, "the subtree deletion still guards the path: no retry against it");

        subtree.Dispose();                                           // the other delete aborts
        signals.Should().Be(1, "the last guard ended: the refused save is posted now");
    }

    /// <summary>NEGATIVE CONTROL — 60f4aca6f's nudge listened to version-guard releases only.</summary>
    [Fact(Timeout = 5000)]
    public void NegativeControl_VersionGuardReleasesAlone_MissTheSubtreeScopesEnd()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var signals = 0;
        using var watch = System.Reactive.Linq.Observable.Where(registry.VersionGuardReleases(Path),
                _ => !registry.IsVersionGuardedDeletionInFlight(Path) && !registry.IsUnderActiveDeletion(Path, out string? _))
            .Subscribe(_ => signals++);
        var subtree = registry.BeginSubtreeDeletion(Path);
        registry.TakeOwnerDeleteSeal("overlap", Path, new FakeLifetime().Bind);
        registry.ReleaseOwnerDeleteSeal("overlap", Path, DeleteSealOutcome.Aborted);
        subtree.Dispose();
        signals.Should().Be(0, "the defect: the subtree scope's end is never announced");
    }

    /// <summary>
    /// Holistic pass: an INDETERMINATE delete's fence failed while the owner lived, and the owner is
    /// then torn down. The fence carries the confirmed state (it can hold an acknowledged write whose
    /// own save the seal refused, which the dispose flush cannot write past the row guard), so the
    /// teardown retries it.
    /// </summary>
    [Fact(Timeout = 5000)]
    public void ATeardownAfterAFailedIndeterminateFence_RetriesTheFence()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new RecentlyDeletedRegistry();
        var owner = new FakeLifetime();
        var runs = 0;
        registry.TakeOwnerDeleteSeal("retry-on-teardown", Path, owner.Bind, _ =>
        {
            runs++;
            return runs == 1
                ? System.Reactive.Linq.Observable.Throw<bool>(new InvalidOperationException("store unavailable"))
                : System.Reactive.Linq.Observable.Return(false);
        }).Should().BeTrue();

        registry.ReleaseOwnerDeleteSeal("retry-on-teardown", Path, DeleteSealOutcome.Indeterminate).Should().BeTrue();
        runs.Should().Be(1);
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeTrue("a failed fence settles nothing");

        owner.OnTeardown.Dispose();
        runs.Should().Be(2, "the owner's teardown is the last chance to fence its confirmed state");
        registry.IsVersionGuardedDeletionInFlight(Path).Should().BeFalse("the retried fence landed");
    }
}

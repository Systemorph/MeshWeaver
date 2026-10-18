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
        registry.IsUnderActiveDeletion(Path, out _).Should().BeFalse("the row guard is lifted so the fence can write");
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
}

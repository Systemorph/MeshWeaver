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
}

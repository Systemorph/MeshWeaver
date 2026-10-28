using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7459 — WHEN the prebuilt-bundle retention lease is released, and what a release
/// fault may do to the pass's outcome. Driven synchronously through subjects: every step is a call the
/// test makes, so the order is pinned and nothing waits.
///
/// <para>The defects: the lease was released on EVERY result of the pass (a pass that emitted and kept
/// working released under itself, so another replica could sweep beside it), an EMPTY pass never
/// released at all (it lapsed), and a release that faulted after a successful sweep replaced the sweep's
/// result with its own fault — the hosted service then recorded a pass fault and logged "nothing was
/// collected" over deletions that had completed.</para>
/// </summary>
public class PrebuiltBundleRetentionLeaseTerminalTest
{
    private sealed class CountingRelease
    {
        private readonly Exception? fault;
        public CountingRelease(Exception? fault = null) => this.fault = fault;
        public int Subscriptions { get; private set; }

        public IObservable<Unit> Release => Observable.Defer(() =>
        {
            Subscriptions++;
            return fault is null ? Observable.Return(Unit.Default) : Observable.Throw<Unit>(fault);
        });
    }

    private sealed class Outcome<T>
    {
        public ImmutableList<T> Values { get; private set; } = ImmutableList<T>.Empty;
        public Exception? Error { get; private set; }
        public bool Completed { get; private set; }
        public void Observe(IObservable<T> source)
            => source.Subscribe(v => Values = Values.Add(v), e => Error = e, () => Completed = true);
    }

    [Fact]
    public void APassThatEmitsAndKeepsWorking_HoldsTheLeaseUntilItCompletes()
    {
        var pass = new Subject<int>();
        var release = new CountingRelease();
        var faults = ImmutableList<Exception>.Empty;
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(pass, release.Release, ex => faults = faults.Add(ex)));

        pass.OnNext(1);
        release.Subscriptions.Should().Be(0,
            "a pass that has emitted and is still working still sweeps — releasing now lets another replica sweep beside it");
        pass.OnNext(2);
        release.Subscriptions.Should().Be(0);

        pass.OnCompleted();
        release.Subscriptions.Should().Be(1, "the lease is released ONCE, at the pass's successful terminal");
        outcome.Values.Should().Equal(1, 2);
        outcome.Completed.Should().BeTrue();
        faults.Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyPass_ReleasesTheLease()
    {
        var release = new CountingRelease();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(Observable.Empty<int>(), release.Release, _ => { }));

        release.Subscriptions.Should().Be(1, "a pass that completed with no result has ended — it must not leave the lease to lapse");
        outcome.Completed.Should().BeTrue();
        outcome.Values.Should().BeEmpty();
    }

    [Fact]
    public void AConsumerThatStopsAtTheFirstResult_StillFindsTheLeaseReleased()
    {
        var release = new CountingRelease();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(Observable.Return(7), release.Release, _ => { }).Take(1));

        outcome.Values.Should().Equal(7);
        release.Subscriptions.Should().Be(1, "results are delivered only after the release was attempted");
    }

    [Fact]
    public void AReleaseThatFaults_AfterASuccessfulSweep_LeavesTheSweepRecorded_AndReportsTheReleaseOnItsOwn()
    {
        var releaseFault = new InvalidOperationException("release failed");
        var release = new CountingRelease(releaseFault);
        var releaseFaults = ImmutableList<Exception>.Empty;
        var records = ImmutableList<string>.Empty;
        var passFaults = ImmutableList<Exception>.Empty;
        var cancellations = 0;
        var ended = false;

        PrebuiltBundleRetentionHostedService.GuardedPass(
                PrebuiltBundleRetentionLease.ReleasedAtTerminal(
                    Observable.Return("collected 3"), release.Release, ex => releaseFaults = releaseFaults.Add(ex)),
                result => records = records.Add(result),
                ex => passFaults = passFaults.Add(ex),
                _ => cancellations++)
            .Subscribe(_ => { }, _ => { }, () => ended = true);

        ended.Should().BeTrue();
        records.Should().Equal(new[] { "collected 3" }, "the deletions completed — the sweep's result must reach the status");
        passFaults.Should().BeEmpty("a release fault is not a pass fault, and 'nothing was collected' would be false");
        releaseFaults.Should().ContainSingle().Which.Should().BeSameAs(releaseFault, "the release failure is reported on its own");
        cancellations.Should().Be(0);
    }

    [Fact]
    public void AFaultedPass_ReleasesOnce_AndSurfacesItsOwnFault_EvenWhenTheReleaseFaultsToo()
    {
        var passFault = new InvalidOperationException("reference read failed");
        var releaseFault = new InvalidOperationException("release failed");
        var release = new CountingRelease(releaseFault);
        var releaseFaults = ImmutableList<Exception>.Empty;
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(
            Observable.Throw<int>(passFault), release.Release, ex => releaseFaults = releaseFaults.Add(ex)));

        release.Subscriptions.Should().Be(1, "a faulted pass has ended — it releases");
        outcome.Error.Should().BeSameAs(passFault, "the pass's own fault is what the pass reports");
        releaseFaults.Should().ContainSingle().Which.Should().BeSameAs(releaseFault);
    }

    [Fact]
    public void ACancelledPass_LeavesTheLeaseToLapse()
    {
        var release = new CountingRelease();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(
            Observable.Throw<int>(new OperationCanceledException("deadline")), release.Release, _ => { }));

        release.Subscriptions.Should().Be(0,
            "a cancelled pass may still be winding down its leaf — handing the store over under it would overlap two sweeps");
        outcome.Error.Should().BeOfType<OperationCanceledException>();
    }
}

using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// 🚨 A one-shot owner-side read must reach a terminal on ALL FOUR of Rx's outcomes — issues #3194
/// and #3195, the successors to #3033 (<see cref="PatchAckTotalityTest"/>).
///
/// <para><b>The defect.</b> <c>Subscribe(onNext, onError)</c> covers two outcomes; #3033 added the
/// third (<c>WhenCompletesEmpty</c>). The fourth — <b>never emits and never completes</b> — was
/// still settled as silence, and the two live sites had it in different shapes:</para>
/// <list type="bullet">
///   <item><b>#3194</b>, the generic patch path's initial base read: NO bound at all, anywhere
///     between the stream and the <c>Subscribe</c>. The only other bounded watcher on that path is
///     armed INSIDE the <c>onNext</c> arm, so a stream that never emits never arms it. A stream's
///     <c>Store</c> is a <c>ReplaySubject</c>, so one that has never published and is never disposed
///     hands <c>Take(1)</c> nothing forever — and the handler had already returned
///     <c>Processed()</c>, so the caller burned its full 31 s bound and reported
///     <c>OwnerUnreachable</c> for a request the owner was still holding.</item>
///   <item><b>#3195</b>, the cold-activation defer: a bound, but only two arms. A COMPLETING primary
///     store passes <c>Take(1)</c> and <b>cancels the Timeout</b> — a terminal notification disposes
///     the timer — so neither arm runs and the leg produces no verdict at all.</item>
/// </list>
///
/// <para><b>What is pinned.</b> <c>DataExtensions.ArmedOneShotRead</c> is a PURE composition over
/// <see cref="IObservable{T}"/>, so all four outcomes are driven on a <see cref="TestScheduler"/>
/// with no mesh and no wall clock. Two properties beyond the four: the empty arm must NOT fire after
/// an emission (a bare completion arm NACKs every successful write — the #3033 twist), and the bound
/// must sit BELOW the caller's filter, because a <c>Timeout</c> over a stream whose emissions are
/// later dropped is re-armed by every dropped one and can never fire. That last one is #3193's
/// unreachable bound, and it is the reason this is a seam rather than three hand-written chains.</para>
/// </summary>
public class OneShotReadTotalityTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>Outcome 1 — it emits. The value passes through and NOTHING else fires.</summary>
    [Fact]
    public void Emission_PassesThrough_AndTheEmptyArmStaysSilent()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var values = new List<int>();
        var errors = new List<Exception>();
        var emptyArms = 0;

        using var sub = source
            .ArmedOneShotRead(Bound, () => emptyArms++, scheduler)
            .Subscribe(values.Add, errors.Add);

        source.OnNext(7);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(60).Ticks);

        values.Should().Equal(7);
        errors.Should().BeEmpty();
        // 🚨 The #3033 twist. Take(1) completes the instant it has its value, while the work started
        // in onNext is still in flight — a bare completion arm here NACKs every successful write.
        emptyArms.Should().Be(0, "an emission preceded the completion");
    }

    /// <summary>Outcome 2 — it errors. The error passes through; the empty arm stays silent.</summary>
    [Fact]
    public void Error_PassesThrough_AndTheEmptyArmStaysSilent()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var errors = new List<Exception>();
        var emptyArms = 0;

        using var sub = source
            .ArmedOneShotRead(Bound, () => emptyArms++, scheduler)
            .Subscribe(_ => { }, errors.Add);

        source.OnError(new InvalidOperationException("boom"));

        errors.Should().HaveCount(1);
        errors[0].Should().BeOfType<InvalidOperationException>();
        emptyArms.Should().Be(0);
    }

    /// <summary>
    /// Outcome 3 — it completes without ever emitting. THE #3195 REGRESSION: the completion passes
    /// <c>Take(1)</c> and cancels the bound, so before the fix neither arm ran and the leg produced
    /// no verdict at all.
    /// </summary>
    [Fact]
    public void EmptyCompletion_RunsTheArm_EvenThoughItCancelsTheBound()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var errors = new List<Exception>();
        var emptyArms = 0;

        using var sub = source
            .ArmedOneShotRead(Bound, () => emptyArms++, scheduler)
            .Subscribe(_ => { }, errors.Add);

        source.OnCompleted();

        emptyArms.Should().Be(1, "the source ended without ever carrying a value");
        errors.Should().BeEmpty("a completion is not a fault");

        // The completion really did cancel the timer: nothing more arrives afterwards.
        scheduler.AdvanceBy(TimeSpan.FromSeconds(60).Ticks);
        emptyArms.Should().Be(1);
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// Outcome 4 — THE #3194 REGRESSION: it neither emits nor completes. Before the fix the generic
    /// path had no bound at all here, so this parked forever and the caller burned its 31 s window.
    /// </summary>
    [Fact]
    public void NeitherEmitsNorCompletes_FaultsAtTheBound()
    {
        var scheduler = new TestScheduler();
        var errors = new List<Exception>();
        var emptyArms = 0;

        // Observable.Never IS a ReplaySubject that has never published and is never disposed.
        using var sub = Observable.Never<int>()
            .ArmedOneShotRead(Bound, () => emptyArms++, scheduler)
            .Subscribe(_ => { }, errors.Add);

        scheduler.AdvanceBy(Bound.Ticks - 1);
        errors.Should().BeEmpty("the bound has not elapsed yet");

        scheduler.AdvanceBy(2);

        errors.Should().HaveCount(1);
        errors[0].Should().BeOfType<TimeoutException>();
        emptyArms.Should().Be(0, "a timeout is not an empty completion — the two verdicts differ");
    }

    /// <summary>
    /// 🚨 #3193's lesson, made structural. The bound sits BELOW the caller's filter, so emissions the
    /// filter DROPS do not re-arm it — a busy stream whose interesting emission never comes still
    /// faults on time. Placing the Timeout above the filter is what made that bound unreachable.
    /// </summary>
    [Fact]
    public void DroppedEmissions_DoNotReArmTheBound()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var errors = new List<Exception>();

        using var sub = source
            .Where(i => i > 100)                       // nothing the source sends will pass
            .ArmedOneShotRead(Bound, () => { }, scheduler)
            .Subscribe(_ => { }, errors.Add);

        // Churn all the way to the edge of the bound; every one of these is dropped by the filter.
        for (var i = 0; i < 9; i++)
        {
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
            source.OnNext(i);
        }
        errors.Should().BeEmpty("9s of churn, bound is 10s");

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks + 1);

        errors.Should().HaveCount(1, "a dropped emission must not extend the wait");
        errors[0].Should().BeOfType<TimeoutException>();
    }

    /// <summary>Control arm: the identical stream WITH a passing emission does not fault — so the
    /// test above cannot be green merely because the composition always times out.</summary>
    [Fact]
    public void APassingEmission_SatisfiesTheSameChain()
    {
        var scheduler = new TestScheduler();
        var source = new Subject<int>();
        var values = new List<int>();
        var errors = new List<Exception>();

        using var sub = source
            .Where(i => i > 100)
            .ArmedOneShotRead(Bound, () => { }, scheduler)
            .Subscribe(values.Add, errors.Add);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(9).Ticks);
        source.OnNext(101);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(60).Ticks);

        values.Should().Equal(101);
        errors.Should().BeEmpty();
    }
}

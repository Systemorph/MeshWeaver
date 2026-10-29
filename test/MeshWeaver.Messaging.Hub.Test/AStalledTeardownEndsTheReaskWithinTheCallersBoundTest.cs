using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Messaging;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 A re-ask held behind a teardown join ends within the CALLER's bound when the teardown stalls
/// (MeshWeaver#7551 review). <c>UpdateRemote</c> holds both of its ShuttingDown re-asks, the early
/// refusal and the late one, behind <see cref="LocalTeardownJoin.WhenLocalTeardownSettled"/>, and both
/// go through <see cref="LocalTeardownJoin.ReaskOnceSettled{T}"/>. A disposal the watchdog finds
/// wedged is named and left alone, so its <c>DisposalCompleted</c> may never fire. Before the bound,
/// the write waited for as long as that lasted: the early refusal never armed the verdict timer, and
/// the late dispatch had already removed the registry entry that timer claims.
/// </summary>
public class AStalledTeardownEndsTheReaskWithinTheCallersBoundTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(31);

    [Fact]
    public void AJoinThatNeverAnswers_FailsAtTheBound_AndNeverReasks()
    {
        var scheduler = new TestScheduler();
        var stalled = new Subject<Unit>();          // a teardown whose disposal never completes
        var reasked = 0;
        var expired = 0;
        Exception? error = null;
        var completed = false;

        using var subscription = stalled
            .ReaskOnceSettled(
                Bound,
                () => { reasked++; return Observable.Return(42); },
                () => { expired++; return new InvalidOperationException("owner unreachable"); },
                scheduler)
            .Subscribe(_ => { }, ex => error = ex, () => completed = true);

        scheduler.AdvanceBy(Bound.Ticks - 1);
        error.Should().BeNull("the caller's bound has not passed yet");
        expired.Should().Be(0);

        scheduler.AdvanceBy(1);
        // A stalled teardown ends the caller's wait with the named refusal, not an open-ended wait.
        var refusal = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("owner unreachable", refusal.Message);
        expired.Should().Be(1, "the hand-off is released exactly once");
        reasked.Should().Be(0, "nothing is re-sent to an owner that may still be registered");
        completed.Should().BeFalse();

        // A teardown that finally completes after the bound changes nothing.
        stalled.OnNext(Unit.Default);
        reasked.Should().Be(0);
        expired.Should().Be(1);
    }

    [Fact]
    public void AJoinThatAnswersInTime_Reasks_AndNeverExpires()
    {
        var scheduler = new TestScheduler();
        var teardown = new Subject<Unit>();
        var reasked = 0;
        var expired = 0;
        int? value = null;
        Exception? error = null;

        using var subscription = teardown
            .ReaskOnceSettled(
                Bound,
                () => { reasked++; return Observable.Return(42); },
                () => { expired++; return new InvalidOperationException("owner unreachable"); },
                scheduler)
            .Subscribe(v => value = v, ex => error = ex);

        scheduler.AdvanceBy(Bound.Ticks - 1);
        reasked.Should().Be(0, "the re-ask waits for the teardown");
        teardown.OnNext(Unit.Default);
        reasked.Should().Be(1);
        value.Should().Be(42);

        scheduler.AdvanceBy(Bound.Ticks);
        error.Should().BeNull();
        expired.Should().Be(0, "a join that answered in time never runs the expiry");
    }
}

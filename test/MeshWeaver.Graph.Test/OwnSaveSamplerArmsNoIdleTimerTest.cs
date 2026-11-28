using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Reactive.Testing;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #5388: every node hub's own-node persistence sampler used <c>Sample(200 ms)</c>, a PERIODIC timer
/// armed at subscribe that fires five times a second for the life of the hub whether or not anything
/// is pending. memex-cloud (4.0.14377) read 2,600-3,500 queued thread-pool items per census as
/// <c>PeriodicTimer =&gt; OwnSaveTicks</c>. The sampler now arms a one-shot window only while a save is
/// pending, and keeps Sample's semantics: one emission per window, the newest state.
/// </summary>
public class OwnSaveSamplerArmsNoIdleTimerTest
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private sealed class CountingScheduler(TestScheduler inner) : IScheduler
    {
        public int Scheduled;
        public DateTimeOffset Now => inner.Now;

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        { Scheduled++; return inner.Schedule(state, action); }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        { Scheduled++; return inner.Schedule(state, dueTime, action); }

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        { Scheduled++; return inner.Schedule(state, dueTime, action); }
    }

    private static MeshNode Node(long version) =>
        new("Doc", "Sampled") { Name = $"v{version}", NodeType = "Markdown", Version = version };

    [Fact]
    public void AnIdleSamplerSchedulesNothing()
    {
        var clock = new TestScheduler();
        var counting = new CountingScheduler(clock);
        var own = new Subject<MeshNode>();
        var posted = new List<MeshNode>();
        using var _ = MeshDataSourceExtensions.OwnSaveTicks(
                own, Observable.Never<Unit>(), new MeshDataSourceExtensions.PendingOwnSave(), null, Interval, counting)
            .Subscribe(posted.Add);

        clock.AdvanceBy(TimeSpan.FromMinutes(10).Ticks);

        Assert.Equal(0, counting.Scheduled);
        Assert.Empty(posted);
    }

    [Fact]
    public void ABurstInsideOneWindowPostsTheNewestStateOnce()
    {
        var clock = new TestScheduler();
        var counting = new CountingScheduler(clock);
        var own = new Subject<MeshNode>();
        var posted = new List<MeshNode>();
        using var _ = MeshDataSourceExtensions.OwnSaveTicks(
                own, Observable.Never<Unit>(), new MeshDataSourceExtensions.PendingOwnSave(), null, Interval, counting)
            .Subscribe(posted.Add);

        own.OnNext(Node(1));
        clock.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        own.OnNext(Node(2));
        clock.AdvanceBy(TimeSpan.FromMilliseconds(50).Ticks);
        own.OnNext(Node(3));
        Assert.Empty(posted);

        clock.AdvanceBy(TimeSpan.FromMilliseconds(100).Ticks);

        Assert.Equal([3L], posted.ConvertAll(n => n.Version));
        Assert.Equal(1, counting.Scheduled);
    }

    [Fact]
    public void ALaterStateOpensANewWindowAndTheTimerStopsBetweenThem()
    {
        var clock = new TestScheduler();
        var counting = new CountingScheduler(clock);
        var own = new Subject<MeshNode>();
        var posted = new List<MeshNode>();
        using var _ = MeshDataSourceExtensions.OwnSaveTicks(
                own, Observable.Never<Unit>(), new MeshDataSourceExtensions.PendingOwnSave(), null, Interval, counting)
            .Subscribe(posted.Add);

        own.OnNext(Node(1));
        clock.AdvanceBy(Interval.Ticks);
        clock.AdvanceBy(TimeSpan.FromMinutes(1).Ticks);
        Assert.Equal(1, counting.Scheduled);

        own.OnNext(Node(2));
        clock.AdvanceBy(Interval.Ticks);

        Assert.Equal([1L, 2L], posted.ConvertAll(n => n.Version));
        Assert.Equal(2, counting.Scheduled);
    }

    [Fact]
    public void ACompletionWithAWindowOpenEmitsThePendingStateThenCompletes()
    {
        var clock = new TestScheduler();
        var source = new Subject<int>();
        var events = new List<string>();
        using var _ = MeshDataSourceExtensions.SampleWhenPending(source, Interval, clock)
            .Subscribe(__ => events.Add("tick"), () => events.Add("completed"));

        source.OnNext(7);
        source.OnCompleted();
        clock.AdvanceBy(Interval.Ticks * 3);

        // The same as Sample(interval): the pending value is flushed once, then the sequence completes.
        Assert.Equal(["tick", "completed"], events);

        var idle = new Subject<int>();
        var idleEvents = new List<string>();
        using var ___ = MeshDataSourceExtensions.SampleWhenPending(idle, Interval, clock)
            .Subscribe(__ => idleEvents.Add("tick"), () => idleEvents.Add("completed"));
        idle.OnCompleted();
        clock.AdvanceBy(Interval.Ticks * 3);
        Assert.Equal(["completed"], idleEvents);
    }

    [Fact]
    public void DisposingTheSamplerCancelsAPendingWindow()
    {
        var clock = new TestScheduler();
        var own = new Subject<MeshNode>();
        var posted = new List<MeshNode>();
        var subscription = MeshDataSourceExtensions.OwnSaveTicks(
                own, Observable.Never<Unit>(), new MeshDataSourceExtensions.PendingOwnSave(), null, Interval, clock)
            .Subscribe(posted.Add);

        own.OnNext(Node(1));
        subscription.Dispose();
        clock.AdvanceBy(Interval.Ticks * 2);

        Assert.Empty(posted);
    }
}

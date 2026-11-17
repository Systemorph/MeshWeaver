using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Fixture;

/// <summary>
/// A virtual <see cref="TimeProvider"/> for a test whose SUBJECT is a framework deadline firing —
/// a request timeout, a deferral budget, a read budget. Time moves only when the test calls
/// <see cref="Advance"/>, so the deadline fires the instant the test decides it should, instead of
/// the test sitting the deadline out in real time on every run (owner directive: a test that only
/// passes by waiting out a real-time window is a defect).
///
/// <para><b>Arm first, then advance.</b> A timer is relative to the clock's time when it is
/// created, so advancing before the code under test has armed its timer moves nothing it owns.
/// <see cref="WhenArmed"/> is the positive signal that the timer exists; advance after it.</para>
///
/// <para>Callbacks run on the thread that calls <see cref="Advance"/>, under the
/// <see cref="ExecutionContext"/> captured when the timer was created — the context
/// <see cref="TimeProvider.System"/>'s timers flow, so an <c>AsyncLocal</c> identity reaches the
/// callback exactly as it does in production. Instance state only: one clock per test.</para>
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private ImmutableList<ManualTimer> timers = ImmutableList<ManualTimer>.Empty;
    private readonly BehaviorSubject<ImmutableList<TimeSpan>> armed = new(ImmutableList<TimeSpan>.Empty);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    /// <summary>
    /// Emits once a timer armed for <paramref name="dueIn"/> (as it was created or last changed) is
    /// pending on this clock — the proof that advancing by <paramref name="dueIn"/> fires it.
    /// </summary>
    /// <param name="dueIn">The due time the code under test arms its timer with.</param>
    /// <returns>A one-shot signal.</returns>
    public IObservable<Unit> WhenArmed(TimeSpan dueIn)
        => armed.Where(list => list.Contains(dueIn)).Select(_ => Unit.Default).Take(1);

    /// <summary>The due times of every timer currently pending, as they were armed.</summary>
    public ImmutableList<TimeSpan> Pending
    {
        get
        {
            lock (gate)
                return timers.Select(t => t.ArmedFor).ToImmutableList();
        }
    }

    /// <summary>
    /// Moves time forward by <paramref name="by"/> and fires — in due order, on this thread — every
    /// timer that has become due.
    /// </summary>
    /// <param name="by">How far to move the clock.</param>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        ImmutableList<ManualTimer> due;
        lock (gate)
        {
            now += by;
            due = timers.Where(t => t.DueAt <= now).OrderBy(t => t.DueAt).ToImmutableList();
            foreach (var timer in due)
            {
                if (timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan)
                    timer.DueAt += timer.Period;
                else
                    timers = timers.Remove(timer);
            }
            Publish();
        }
        foreach (var timer in due)
            timer.Fire();
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state, ExecutionContext.Capture());
        timer.Change(dueTime, period);
        return timer;
    }

    private void Arm(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            timers = timers.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.ArmedFor = dueTime;
                timer.DueAt = now + dueTime;
                timer.Period = period;
                timers = timers.Add(timer);
            }
            Publish();
        }
    }

    private void Disarm(ManualTimer timer)
    {
        lock (gate)
        {
            timers = timers.Remove(timer);
            Publish();
        }
    }

    // Under `gate`, so snapshots are published in the order the state changed.
    private void Publish() => armed.OnNext(timers.Select(t => t.ArmedFor).ToImmutableList());

    private sealed class ManualTimer(
        ManualTimeProvider clock, TimerCallback callback, object? state, ExecutionContext? context) : ITimer
    {
        public TimeSpan ArmedFor { get; set; }
        public DateTimeOffset DueAt { get; set; }
        public TimeSpan Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Arm(this, dueTime, period);
            return true;
        }

        public void Fire()
        {
            if (context is null)
                callback(state);
            else
                ExecutionContext.Run(context, s => callback(s), state);
        }

        public void Dispose() => clock.Disarm(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

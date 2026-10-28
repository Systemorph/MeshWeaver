#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Security;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The LIVE inputs of the declared point read (MeshWeaver#7367), measured on the exact compositions
/// both seams run — the standard read's input set (<see cref="DeclaredPointRead.AttachedThenSeeded{T}"/>)
/// and the ownership re-check of a stored type's partition (<see cref="DeclaredPointRead.FollowOwnership"/>).
///
/// <list type="bullet">
///   <item><description><b>The setup window.</b> The first decision was seeded with <c>StartWith</c>,
///     which emits its seed BEFORE it subscribes its source: the first read ran before the invalidation
///     feed's handler was attached, so a commit that landed after the read captured its value was
///     announced to nobody and the old decision stood until some other signal arrived. The feed here
///     is a hot subject — exactly like the process feed, an announcement made while no handler is
///     attached reaches no one — and the commit lands INSIDE the read, after it captured its value: the
///     window, deterministically.</description></item>
///   <item><description><b>The unbounded re-read.</b> The ownership re-read on a signal had no budget,
///     so a re-read that stalled after a withdrawal kept the previous decision in force. Each re-read
///     is now bounded until its first answer; past the budget the partition is not owned.</description></item>
/// </list>
/// </summary>
public class DeclaredReadLiveInputsTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_input_set_is_attached_before_the_first_evaluation_is_seeded()
    {
        var feed = new Subject<bool>();
        var seen = ImmutableList<bool>.Empty;
        var committed = false;
        // The first evaluation (the seed, false) is where the commit lands: after it read its inputs.
        using var _ = DeclaredPointRead.AttachedThenSeeded(feed, false).Subscribe(withdraw =>
        {
            seen = seen.Add(withdraw);
            if (!withdraw && !committed)
            {
                committed = true;
                feed.OnNext(true);
            }
        });

        Assert.True(committed, "INSTRUMENT: the commit landed inside the first evaluation");
        Assert.Equal(new[] { false, true }, seen);
    }

    [Fact]
    public void Instrument_a_seed_emitted_before_its_signals_are_attached_loses_a_commit_in_the_window()
    {
        // NEGATIVE CONTROL of the instrument: the shape the seams used to have, under the same commit,
        // never hears it — so the test above measures the setup window, not a commit that arrives anyway.
        var feed = new Subject<bool>();
        var seen = ImmutableList<bool>.Empty;
        var committed = false;
        using var _ = feed.StartWith(false).Subscribe(withdraw =>
        {
            seen = seen.Add(withdraw);
            if (!withdraw && !committed)
            {
                committed = true;
                feed.OnNext(true);
            }
        });

        Assert.True(committed);
        Assert.Equal(new[] { false }, seen);
    }

    [Fact]
    public void An_ownership_withdrawal_committed_after_the_first_read_captured_its_value_is_followed()
    {
        var scheduler = new TestScheduler();
        var changed = new Subject<Unit>();
        var owned = true;
        var reads = 0;
        // One read of the ownership: it captures the CURRENT value and answers it; the first time, the
        // withdrawal commits (and is announced) right after the value was captured.
        IObservable<bool> Read() => Observable.Create<bool>(observer =>
        {
            var captured = owned;
            reads++;
            observer.OnNext(captured);
            if (reads == 1)
            {
                owned = false;
                changed.OnNext(Unit.Default);
            }
            return System.Reactive.Disposables.Disposable.Empty;
        });

        var seen = ImmutableList<bool>.Empty;
        using var _ = DeclaredPointRead.FollowOwnership(changed, Read, Budget, scheduler).Subscribe(v => seen = seen.Add(v));

        Assert.Equal(2, reads);
        Assert.Equal(new[] { true, false }, seen);
    }

    [Fact]
    public void An_ownership_that_does_not_change_is_read_once_and_stays_owned()
    {
        // POSITIVE CONTROL: the same read with no commit in the window — one read, one decision.
        var scheduler = new TestScheduler();
        var changed = new Subject<Unit>();
        var reads = 0;
        IObservable<bool> Read() => Observable.Defer(() =>
        {
            reads++;
            return Observable.Return(true).Concat(Observable.Never<bool>());
        });

        var seen = ImmutableList<bool>.Empty;
        using var _ = DeclaredPointRead.FollowOwnership(changed, Read, Budget, scheduler).Subscribe(v => seen = seen.Add(v));
        scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

        Assert.Equal(1, reads);
        Assert.Equal(new[] { true }, seen);
    }

    [Fact]
    public void A_stalled_ownership_reread_ends_not_owned_at_the_budget_and_holds_the_decision_until_then()
    {
        var scheduler = new TestScheduler();
        var changed = new Subject<Unit>();
        // The first read answers owned; the re-read after the signal never answers.
        var answers = ImmutableArray.Create(Observable.Return(true).Concat(Observable.Never<bool>()), Observable.Never<bool>());
        var reads = 0;
        var stalls = 0;
        var seen = ImmutableList<bool>.Empty;
        using var _ = DeclaredPointRead.FollowOwnership(changed, () => answers[reads++], Budget, scheduler, () => stalls++)
            .Subscribe(v => seen = seen.Add(v));

        changed.OnNext(Unit.Default);
        scheduler.AdvanceBy(Budget.Ticks - 1);
        // A re-read in flight withdraws nothing: a sync that rewrites the config does not blink an open read.
        Assert.Equal(new[] { true }, seen);

        scheduler.AdvanceBy(1);
        Assert.Equal(new[] { true, false }, seen);
        Assert.Equal(1, stalls);
    }

    [Fact]
    public void An_ownership_reread_answered_within_the_budget_decides_and_no_deadline_fires()
    {
        // CONTROL: the budget bounds a re-read until its first answer, never the live follow after it.
        var scheduler = new TestScheduler();
        var changed = new Subject<Unit>();
        var answers = ImmutableArray.Create(
            Observable.Return(true).Concat(Observable.Never<bool>()),
            Observable.Timer(TimeSpan.FromSeconds(5), scheduler).Select(_ => true).Concat(Observable.Never<bool>()));
        var reads = 0;
        var stalls = 0;
        var seen = ImmutableList<bool>.Empty;
        using var _ = DeclaredPointRead.FollowOwnership(changed, () => answers[reads++], Budget, scheduler, () => stalls++)
            .Subscribe(v => seen = seen.Add(v));

        changed.OnNext(Unit.Default);
        scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);

        Assert.Equal(new[] { true }, seen);
        Assert.Equal(0, stalls);
    }
}

#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh.Security;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// How a followed standing Read treats a re-check IN FLIGHT (the pending marker, <c>null</c>), on a
/// direct owner-side subscription (MeshWeaver#7298; review of #7329).
///
/// <list type="bullet">
///   <item><description>A pending PERIOD has one deadline however many access commits withdraw it
///     again (<see cref="DeclaredPointRead.BoundPendingPeriods"/>): each commit replaces the
///     evaluation in flight and restarts that evaluation's timeout, so a stalled rule under commits
///     faster than the budget would otherwise stay pending — and an admitted subscription holding on
///     it would stay open — forever.</description></item>
///   <item><description>The pending marker CANCELS a declaration check started under the withdrawn
///     denial (<see cref="DeclaredReadSubscriptionFollow.VerdictOf"/>): filtered out ahead of the
///     switch, it let that check answer <c>true</c> and forward a revision while the verdict was being
///     re-decided.</description></item>
/// </list>
/// </summary>
public class StandingReadPendingTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void A_pending_period_ends_undetermined_at_its_deadline_however_many_commits_withdraw_it_again()
    {
        var scheduler = new TestScheduler();
        var standing = new Subject<PermissionCheckOutcome?>();
        var seen = ImmutableList<PermissionCheckOutcome?>.Empty;
        using var _ = DeclaredPointRead.BoundPendingPeriods(standing, Budget, "p/doc", scheduler).Subscribe(o => seen = seen.Add(o));

        standing.OnNext(PermissionCheckOutcome.Granted);
        standing.OnNext(null);
        // Further commits keep withdrawing the stalled re-check, each well inside the budget.
        for (var i = 0; i < 4; i++)
        {
            scheduler.AdvanceBy(TimeSpan.FromSeconds(3).Ticks);
            standing.OnNext(null);
        }
        // 12 s after the period began: past its deadline although no single gap exceeded it.
        Assert.Equal(3, seen.Count);
        Assert.Equal(PermissionCheckOutcome.Granted, seen[0]);
        Assert.Null(seen[1]);
        Assert.True(seen[2]!.IsUndetermined, "a pending period that outlives the budget is undetermined — refused");
        Assert.False(seen[2]!.IsGranted);
    }

    [Fact]
    public void A_pending_period_settled_inside_the_budget_ends_on_its_verdict_and_no_deadline_fires()
    {
        // Control: the deadline is per PERIOD, never a blind timer — a re-check that settles in time
        // decides, and nothing undetermined follows it.
        var scheduler = new TestScheduler();
        var standing = new Subject<PermissionCheckOutcome?>();
        var seen = ImmutableList<PermissionCheckOutcome?>.Empty;
        using var _ = DeclaredPointRead.BoundPendingPeriods(standing, Budget, "p/doc", scheduler).Subscribe(o => seen = seen.Add(o));

        standing.OnNext(PermissionCheckOutcome.Granted);
        standing.OnNext(null);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(5).Ticks);
        standing.OnNext(PermissionCheckOutcome.Granted);
        scheduler.AdvanceBy(TimeSpan.FromSeconds(60).Ticks);

        Assert.Equal(new PermissionCheckOutcome?[] { PermissionCheckOutcome.Granted, null, PermissionCheckOutcome.Granted }, seen);
    }

    [Fact]
    public void A_declaration_check_started_under_a_withdrawn_denial_cannot_admit_while_the_recheck_is_pending()
    {
        var standing = new Subject<PermissionCheckOutcome?>();
        var declaration = new Subject<bool>();
        var verdicts = ImmutableList<bool>.Empty;
        using var _ = DeclaredReadSubscriptionFollow.VerdictOf(standing, () => declaration).Subscribe(v => verdicts = verdicts.Add(v));

        standing.OnNext(PermissionCheckOutcome.Denied);
        Assert.True(declaration.HasObservers, "a definitive denial hands the decision to the declaration");
        standing.OnNext(null);
        Assert.False(declaration.HasObservers, "the pending marker cancels the declaration check started under the withdrawn denial");

        declaration.OnNext(true);
        Assert.Empty(verdicts);

        standing.OnNext(PermissionCheckOutcome.Undetermined("stalled"));
        Assert.Equal(new[] { false }, verdicts);
    }

    [Fact]
    public void Negative_control_the_filtered_composition_admits_on_the_withdrawn_denial()
    {
        // The composition this replaces: the pending marker filtered out ahead of the switch. If this
        // ever stops admitting, the regression above no longer exercises the trap.
        var standing = new Subject<PermissionCheckOutcome?>();
        var declaration = new Subject<bool>();
        var verdicts = ImmutableList<bool>.Empty;
        using var _ = standing
            .Where(outcome => outcome is not null)
            .Select(outcome => outcome!.IsGranted
                ? Observable.Return(true)
                : outcome.IsUndetermined ? Observable.Return(false) : declaration)
            .Switch()
            .Subscribe(v => verdicts = verdicts.Add(v));

        standing.OnNext(PermissionCheckOutcome.Denied);
        standing.OnNext(null);
        declaration.OnNext(true);

        Assert.Equal(new[] { true }, verdicts);
    }

    [Fact]
    public void A_pending_marker_holds_an_admitted_verdict_and_a_settled_grant_admits_again()
    {
        var standing = new Subject<PermissionCheckOutcome?>();
        var verdicts = ImmutableList<bool>.Empty;
        using var _ = DeclaredReadSubscriptionFollow.VerdictOf(standing, () => Observable.Return(false)).Subscribe(v => verdicts = verdicts.Add(v));

        standing.OnNext(PermissionCheckOutcome.Granted);
        standing.OnNext(null);
        Assert.Equal(new[] { true }, verdicts);
        standing.OnNext(PermissionCheckOutcome.Granted);
        Assert.Equal(new[] { true, true }, verdicts);
    }
}

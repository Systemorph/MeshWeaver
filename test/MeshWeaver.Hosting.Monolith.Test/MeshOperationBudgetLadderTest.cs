using System;
using System.Collections.Generic;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A NESTED BOUND THAT CANNOT FIRE FIRST IS NOT A BOUND — issue #1198.
///
/// <para>The delete path bounds three levels that OVERLAP in time: the operation's own stages, a
/// handler running inside one of those stages (a descendant answering the pre-flight fan-out, or a
/// cascade leg re-entering the delete handler), and the authorization fold inside that handler.
/// Only the innermost level knows WHICH read starved; the outer ones can say no more than "the
/// operation ran out of time". So the inner one has to be the one that gives up.</para>
///
/// <para><b>It was not.</b> All three read 30 s — <c>MeshOperationOptions.Timeout</c> twice and
/// <c>RowLevelSecurityOptions.PermissionEstablishmentBudget</c> once: three constants, configured
/// independently, equal by coincidence. Equal is not an ordering — the OUTER clock always starts
/// first — so the inner bound could never win, and every starved delete reported the anonymous
/// timeout #1198 has been staring at.</para>
///
/// <para><b>What this suite pins is the RULE, not the numbers.</b> There is exactly one configured
/// value and every nested rung is derived from it by <see cref="MeshOperationOptions.Nest"/>, which
/// contracts strictly — so the ordering holds for every configuration rather than for the one that
/// happens to be checked in, and it cannot drift apart again because there is nothing left to drift
/// against. A test asserting only "20 &lt; 25 &lt; 30" would have passed just as happily on the
/// three equal 30s that caused the bug.</para>
/// </summary>
public class MeshOperationBudgetLadderTest
{
    /// <summary>
    /// Every budget a host could plausibly configure, from a test's impatient millisecond to a
    /// batch job's hour — plus the production default. The rule has to hold across all of them,
    /// because "it holds for the default" is precisely the assurance #1198 already had.
    /// </summary>
    public static TheoryData<int> ConfiguredMilliseconds => new()
    {
        1, 2, 7, 100, 999, 1_000, 5_000, 10_000, 15_000, 30_000, 60_000, 600_000, 3_600_000
    };

    [Theory]
    [MemberData(nameof(ConfiguredMilliseconds))]
    public void TheLadderContractsStrictly_AtEveryConfiguredBudget(int milliseconds)
    {
        var opts = new MeshOperationOptions { Timeout = TimeSpan.FromMilliseconds(milliseconds) };

        opts.NestedTimeout.Should().BeLessThan(opts.Timeout,
            "a handler that only runs because a caller is already holding a bound open must give "
            + "up before that caller does — otherwise the caller's anonymous timeout is the only "
            + "thing anyone ever sees (#1198)");
        opts.PermissionEstablishmentBudget.Should().BeLessThan(opts.NestedTimeout,
            "the authorization fold is the deepest level and the only one that knows WHICH read "
            + "starved; if the handler around it gives up first, that knowledge is discarded");
        opts.PermissionEstablishmentBudget.Should().BeGreaterThan(TimeSpan.Zero,
            "a rung that collapses to zero fires before the work it bounds has begun, which turns "
            + "an availability report into an unconditional refusal");
    }

    /// <summary>
    /// The production default, spelled out — not because the numbers are the rule, but because
    /// whoever reads the next incident log needs to recognise them, and because the deepest rung
    /// has to stay an order of magnitude above a healthy cold permission fold (sub-second warm,
    /// low seconds cold). Contracting far enough to be safe is easy; the cost of OVER-contracting
    /// is a correctly-entitled caller told the check could not be established.
    /// </summary>
    [Fact]
    public void TheProductionDefault_IsThirtyTwentyFiveTwenty()
    {
        var opts = new MeshOperationOptions();

        opts.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        opts.NestedTimeout.Should().Be(TimeSpan.FromSeconds(25));
        opts.PermissionEstablishmentBudget.Should().Be(TimeSpan.FromSeconds(20));
    }

    /// <summary>
    /// 🚨 The collision has to be UNREPRESENTABLE, not merely absent. #1198's three equal budgets
    /// were each individually legal — nothing anywhere said they were supposed to be ordered, so
    /// nothing could notice when they were not. The contraction parameters are now the only way to
    /// express a non-contracting ladder, and both refuse.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonContractingReserve_IsRefused(int seconds)
        => ((Action)(() => _ = new MeshOperationOptions
        {
            NestingReserve = TimeSpan.FromSeconds(seconds)
        })).Should().Throw<ArgumentOutOfRangeException>(
            "a reserve of zero or less makes Nest() return its own argument — a nested bound equal "
            + "to the one enclosing it, which is exactly the defect");

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(-0.5d)]
    public void ANonContractingFraction_IsRefused(double fraction)
        => ((Action)(() => _ = new MeshOperationOptions
        {
            MinNestingFraction = fraction
        })).Should().Throw<ArgumentOutOfRangeException>(
            "at or above one the floor stops contracting; at or below zero it collapses the rung");

    /// <summary>
    /// <see cref="MeshOperationOptions.Nest"/> is the single primitive the whole ladder is built
    /// from, so its contract is asserted directly: strictly contracting, and never collapsing to
    /// nothing — including at the awkward sizes either side of the reserve, where the absolute
    /// branch and the fractional floor swap over.
    /// </summary>
    [Fact]
    public void Nest_ContractsStrictly_ForEveryEnclosingBound()
    {
        var opts = new MeshOperationOptions();
        var enclosing = new List<TimeSpan>
        {
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10)
        };

        foreach (var t in enclosing)
        {
            var nested = opts.Nest(t);
            nested.Should().BeLessThan(t, $"Nest({t}) must contract");
            nested.Should().BeGreaterThan(TimeSpan.Zero, $"Nest({t}) must stay usable");
        }
    }
}

using System;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The whole ladder, not one rung of it</b> (issue #1198).
///
/// <para><b>What #1198 actually was.</b> Three levels of the delete path were three independently
/// configured constants that all happened to read 30 s. Equal budgets are not an ordering — the
/// outer clock starts first — so the INNERMOST bound, the only one that knows WHICH read starved,
/// could never fire. Every failure was therefore reported by the outermost bound, which can say no
/// more than "the operation ran out of time". `MeshOperationOptions` closed it by configuring
/// exactly ONE value and DERIVING every nested bound through <see cref="MeshOperationOptions.Nest"/>,
/// which is strictly contracting.</para>
///
/// <para><b>Why this test exists on top of that.</b> The invariant is stated in
/// <c>MeshOperationOptions</c>'s own summary —
/// <c>QueryInitialBudget &lt; PermissionEstablishmentBudget &lt; NestedTimeout &lt; Timeout</c> for
/// EVERY configuration — and exactly one pair of it was pinned by a test
/// (<c>QueryFanInStallIsTerminalTest.TheFanInsRung_IsStrictlyInsideTheFoldsBudget</c>, the innermost
/// two). A test naming ONE member of an ordered set cannot see a regression in the others: a change
/// that made <c>NestedTimeout</c> equal <c>Timeout</c> — the exact 30 = 30 shape #1198 was — passes
/// it, and reproduces the defect on the two rungs that produced the production occurrences.</para>
///
/// <para><b>The two regimes.</b> <see cref="MeshOperationOptions.Nest"/> takes the LARGER of an
/// absolute reserve and a fraction, so which term governs depends on the enclosing value: at the
/// production default the reserve does, and for a small budget the fraction does. Both are
/// parameterised here, because a contraction proved only where the reserve governs says nothing
/// about the regime a test fixture actually configures — and test fixtures configure small
/// budgets.</para>
///
/// <para>Pure and deterministic: no mesh, no hub, no IO, nothing to flake.</para>
/// </summary>
public class TheBudgetLadderContractsAtEveryRungTest
{
    /// <summary>
    /// Budgets spanning both regimes: the production default and two above it where
    /// <c>NestingReserve</c> (5 s) is the larger term, and three small ones where
    /// <c>MinNestingFraction</c> (0.5) is.
    /// </summary>
    public static TheoryData<int> Budgets => new(300_000, 60_000, 30_000, 8_000, 1_000, 20);

    /// <summary>
    /// 🚨 THE INVARIANT, all four rungs at once. Adjacent-pair assertions, so a failure names the
    /// pair that collapsed rather than only that "the ladder is wrong".
    /// </summary>
    [Theory]
    [MemberData(nameof(Budgets))]
    public void EveryRung_IsStrictlyInsideTheOneEnclosingIt(int budgetMs)
    {
        var options = new MeshOperationOptions { Timeout = TimeSpan.FromMilliseconds(budgetMs) };

        options.NestedTimeout.Should().BeLessThan(options.Timeout,
            "rung 2 runs INSIDE a rung-1 operation — a cascade leg, or one leg of the pre-flight "
            + "fan-out — so it has to be able to give up first. Equal is what #1198 was: the outer "
            + "clock starts first, so an equal inner bound never fires and the silent leaf is never "
            + "named");
        options.PermissionEstablishmentBudget.Should().BeLessThan(options.NestedTimeout,
            "rung 3 is the authorization fold inside a rung-2 handler; only it can answer "
            + "Unavailable rather than letting its caller report a generic timeout");
        options.QueryInitialBudget.Should().BeLessThan(options.PermissionEstablishmentBudget,
            "rung 4 is the query fan-in the rung-3 fold is WAITING ON — which provider starved is "
            + "knowable only here, and an equal bound loses that attribution to a coin flip");

        options.QueryInitialBudget.Should().BePositive(
            "a non-positive innermost bound would fire instantly and turn every read into an "
            + "availability failure — the contraction has to stay inside the positive domain");
    }

    /// <summary>
    /// The property the ladder is BUILT on, asserted directly rather than only through the four
    /// rungs: <c>Nest</c> is strictly contracting on its whole declared domain. The rungs are three
    /// samples of this; a regression in <c>Nest</c> that happened to leave those three ordered would
    /// still be a defect for any other caller of it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Budgets))]
    public void Nest_IsStrictlyContracting_AndIteratingItNeverReachesZero(int budgetMs)
    {
        var options = new MeshOperationOptions { Timeout = TimeSpan.FromMilliseconds(budgetMs) };
        var enclosing = options.Timeout;

        // Iterate well past the four rungs the platform uses: a future rung 5 must be able to nest
        // inside rung 4 by the same rule, and this is what says the rule supports it.
        //
        // 🚨 The SEED is in the set on purpose: the uniqueness assertion below is what catches a
        // collapse between rung 1 and rung 2 — the exact 30 = 30 shape #1198 was — so dropping
        // `Timeout` itself would quietly remove the pair this test exists for.
        var seen = ImmutableList.Create(enclosing);
        for (var level = 0; level < 12; level++)
        {
            var nested = options.Nest(enclosing);
            nested.Should().BeLessThan(enclosing,
                $"Nest must contract at level {level + 1} — the ladder's whole guarantee is that a "
                + "bound nested inside another is strictly quicker to give up");
            nested.Should().BePositive(
                $"level {level + 1} collapsed to zero or below; the contraction must stay inside "
                + "the positive domain or a nested bound becomes an instant refusal");
            seen = seen.Add(nested);
            enclosing = nested;
        }

        seen.Should().OnlyHaveUniqueItems(
            "two rungs reading the same value IS the #1198 defect — it does not matter that they "
            + "are far apart in the ladder, an equal pair cannot be ordered");
    }

    /// <summary>
    /// The domain guard, because the contraction is only PROVABLE above it. A sub-millisecond
    /// budget is refused at the setter rather than silently truncating two rungs onto the same tick,
    /// which would put the ladder back where #1198 found it.
    /// </summary>
    [Fact]
    public void ASubMillisecondBudget_IsRefused_NotSilentlyTruncated()
    {
        // Assert.Throws rather than a fluent Throw: the house assertion library
        // (MeshWeaver.Reactive.Assertions) carries no exception assertion, and reaching for the
        // real FluentAssertions here would mix two libraries in one file.
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => new MeshOperationOptions { Timeout = TimeSpan.FromTicks(1) });

        refusal.Message.Should().Contain("1 ms",
            "the refusal is the alternative to two rungs colliding on one tick, and it has to say "
            + "what the domain IS — a configuration error that does not name the bound is a bound "
            + "the next reader configures wrong again");
    }
}

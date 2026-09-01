using System;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit contract for <see cref="OverlayHealBudget"/> — the spacing that survives the recycle a
/// compilation-overlay self-heal orders (issue #1814 defect B). The watcher cannot hold this state
/// itself: its heal disposes the hub that owns it, so the replacement hub's watcher would restart
/// at the first rung for ever if the pair never converges.
/// </summary>
public class OverlayHealBudgetTest
{
    private const string Instance = "Edu";
    private const string NodeType = "Store/Plugin";

    private static readonly DateTimeOffset T0 =
        new(2026, 8, 17, 20, 46, 0, TimeSpan.Zero);

    /// <summary>
    /// The overwhelmingly common case — a deploy window that cleared — must not be slowed down: a
    /// pair with no recent self-heal recycles the instant it has a usable build.
    /// </summary>
    [Fact]
    public void FirstHeal_IsNeverDelayed()
    {
        var budget = new OverlayHealBudget();

        budget.EarliestHeal(Instance, NodeType, T0).Should().Be(DateTimeOffset.MinValue);
        budget.HealsSoFar(Instance, NodeType, T0).Should().Be(0);
    }

    /// <summary>
    /// Repeats inside the window widen: 45s, 90s, 3m, 6m, then 10m for ever. The ladder holds at
    /// its last rung rather than stopping — a budget that ran out would re-create the latch.
    /// </summary>
    [Fact]
    public void RepeatedHeals_WidenTheSpacing_AndHoldAtTheCeiling()
    {
        var budget = new OverlayHealBudget();
        var expected = new[]
        {
            TimeSpan.FromSeconds(45),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(6),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10),
        };

        var now = T0;
        for (var i = 0; i < expected.Length; i++)
        {
            budget.RecordHeal(Instance, NodeType, now).Should().Be(i + 1);
            budget.EarliestHeal(Instance, NodeType, now).Should().Be(now + expected[i],
                "self-heal #{0} earns the next one a {1} wait", i + 1, expected[i]);
            // Advance to the point the spacing allows, which is where the next heal lands.
            now += expected[i];
        }
    }

    /// <summary>
    /// A pair that healed once and then stayed healthy is forgotten, so a genuinely new incident
    /// later on is not charged for the old one.
    /// </summary>
    [Fact]
    public void APairThatStaysHealthy_IsForgotten()
    {
        var budget = new OverlayHealBudget();
        budget.RecordHeal(Instance, NodeType, T0);

        var justInside = T0 + OverlayHealBudget.ForgetWindow - TimeSpan.FromSeconds(1);
        budget.EarliestHeal(Instance, NodeType, justInside).Should().NotBe(DateTimeOffset.MinValue);
        budget.HealsSoFar(Instance, NodeType, justInside).Should().Be(1);

        var past = T0 + OverlayHealBudget.ForgetWindow;
        budget.EarliestHeal(Instance, NodeType, past).Should().Be(DateTimeOffset.MinValue);
        budget.HealsSoFar(Instance, NodeType, past).Should().Be(0);
        // …and the next heal counts from one again, not from two.
        budget.RecordHeal(Instance, NodeType, past).Should().Be(1);
    }

    /// <summary>
    /// The budget is keyed by the PAIR. One instance stuck on one type must not slow the recovery
    /// of a different instance, or of the same instance on a different type — the 2026-08-17 blast
    /// radius was twelve independent roots, and charging them to one counter would have serialised
    /// twelve unrelated recoveries.
    /// </summary>
    [Fact]
    public void SpacingIsPerInstanceAndType()
    {
        var budget = new OverlayHealBudget();
        budget.RecordHeal(Instance, NodeType, T0);

        budget.EarliestHeal("Chess", NodeType, T0).Should().Be(DateTimeOffset.MinValue);
        budget.EarliestHeal(Instance, "Edu/Module", T0).Should().Be(DateTimeOffset.MinValue);
        budget.EarliestHeal(Instance, NodeType, T0).Should().Be(T0 + TimeSpan.FromSeconds(45));
    }
}

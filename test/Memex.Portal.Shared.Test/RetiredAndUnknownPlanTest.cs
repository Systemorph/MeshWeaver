using System;
using System.Collections.Immutable;
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 A RETIRED plan id resolves to its successor, explicitly; an UNKNOWN plan id stays unknown and
/// is named (#5894).
///
/// <para>Measured on the public registry: a dedicated client instance stored the plan <c>sme</c>.
/// The plan had been renamed to <c>dedicated</c> in the licence documents only and its tier node
/// <c>Admin/Tiers/sme</c> deleted, so the ladder no longer knew <c>sme</c>;
/// <see cref="PlanTierRanks.CoversInstance"/> decided it at the baseline, and the instance's ledger
/// listed 18 refused packages — Mail and Teams among them — each as "this instance is on free".
/// Nothing said the stored plan was unknown.</para>
/// </summary>
public class RetiredAndUnknownPlanTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The ladder as the registry reads it AFTER <c>Admin/Tiers/sme</c> was deleted:
    /// free 0 · personal 10 · pro 20 · dedicated 25 (all-access) · enterprise 30.</summary>
    private static readonly PlanTierRanks Ladder = PlanTierRanks.From(new[]
    {
        ("free", 0, false), ("personal", 10, false), ("pro", 20, false),
        ("dedicated", 25, true), ("enterprise", 30, false),
    });

    [Fact]
    public void TheRetiredSmeId_ResolvesToDedicated_AndCoversWhatDedicatedCovers()
    {
        Assert.Equal("dedicated", PlanTierRanks.Canonical("sme"));
        Assert.Equal("dedicated", PlanTierRanks.Canonical("  SME "));
        Assert.Equal("dedicated", PlanTierRanks.SuccessorOf("sme"));

        // The defect: an instance on `sme` was refused every paid package. Dedicated is all-access.
        Assert.True(Ladder.CoversInstance("sme", "enterprise"),
            "an instance still storing the retired id stands on its successor, which is all-access");
        Assert.False(Ladder.IsUnknownPlan("sme"), "a retired id is not unknown — it has a successor");
    }

    [Fact]
    public void ACurrentId_HasNoSuccessor_AndCanonicalLeavesItAlone()
    {
        foreach (var id in new[] { "free", "personal", "pro", "dedicated", "enterprise" })
        {
            Assert.Null(PlanTierRanks.SuccessorOf(id));
            Assert.Equal(id, PlanTierRanks.Canonical(id));
        }
    }

    [Fact]
    public void ATierNodeStillCarryingTheRetiredId_DoesNotRedefineItsSuccessor()
    {
        // `sme` enumerated AFTER dedicated with a different rank and no all-access flag: if the
        // ladder folded it onto `dedicated`, dedicated would lose its all-access standing.
        var ladder = PlanTierRanks.From(new[] { ("dedicated", 25, true), ("sme", 15, false) });

        Assert.Equal(25, ladder.RankOf("dedicated"));
        Assert.True(ladder.IsAllAccess("dedicated"));
        Assert.Single(ladder.Ids);
    }

    [Fact]
    public void AnUnknownPlan_IsNamedUnknown_AndStillDecidesAtTheBaseline()
    {
        Assert.True(Ladder.IsUnknownPlan("gold"));
        // Fail closed, unchanged: an unknown plan never widens a licence.
        Assert.False(Ladder.CoversInstance("gold", "pro"));
        Assert.True(Ladder.CoversInstance("gold", "free"));

        // The controls: none of these is unknown.
        Assert.False(Ladder.IsUnknownPlan(null));
        Assert.False(Ladder.IsUnknownPlan(""));
        Assert.False(Ladder.IsUnknownPlan("free"));
        Assert.False(Ladder.IsUnknownPlan("PRO"));
        // A registry with no ladder at all: free is still the baseline, not unknown.
        Assert.False(PlanTierRanks.Empty.IsUnknownPlan("free"));
    }

    [Fact]
    public void ARefusalForAnUnknownInstancePlan_NamesTheStoredPlan_NotFree()
    {
        var grant = new PluginGrant
        {
            InstanceId = "client",
            Entries = ImmutableList.Create(new PluginGrantEntry { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages }),
        };

        // Before #5894 this read "free" — the sentence then said "this instance is on free" for an
        // instance whose record says something else entirely.
        Assert.Equal("gold", grant.TierRefusal("Plugins", "Mail", "pro", Ladder, "gold", Now));
        // A known plan is reported as before.
        Assert.Equal("free", grant.TierRefusal("Plugins", "Mail", "pro", Ladder, "free", Now));
        Assert.Equal("free", grant.TierRefusal("Plugins", "Mail", "pro", Ladder, null, Now));
        // The retired id is covered by its all-access successor: nothing is refused at all.
        Assert.Null(grant.TierRefusal("Plugins", "Mail", "enterprise", Ladder, "sme", Now));
    }

    [Fact]
    public void AnUnknownCap_IsStillReportedAsTheBaseline()
    {
        // The cap case is unchanged on purpose: a cap the ladder does not know is neither the
        // instance's plan nor an upgrade target, so the sentence names the level that decided.
        var grant = new PluginGrant
        {
            InstanceId = "client",
            Entries = ImmutableList.Create(new PluginGrantEntry
                { Source = "Plugins", PackageId = PluginGrantEntry.AllPackages, Tier = "gold" }),
        };

        Assert.Equal("free", grant.TierRefusal("Plugins", "Mail", "pro", Ladder, "pro", Now));
    }

    [Fact]
    public void TheDeletionGuard_CountsABlankPlanAsTheBaseline_AndTheRetiredIdAsItsSuccessor()
    {
        Assert.True(TierInUseDeletionGuard.StandsOn(new MeshWeaverInstance { InstanceId = "a" }, "free"));
        Assert.True(TierInUseDeletionGuard.StandsOn(new MeshWeaverInstance { InstanceId = "b", Plan = "sme" }, "dedicated"));
        Assert.False(TierInUseDeletionGuard.StandsOn(new MeshWeaverInstance { InstanceId = "c", Plan = "pro" }, "free"));
    }
}

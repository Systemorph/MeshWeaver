

using MeshWeaver.Graph.Configuration;
using MeshWeaver.PluginCatalog;
using Xunit;
using Seed = MeshWeaver.Graph.Configuration.PrebuiltAssemblySeeder.SeedOutcome;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// <c>/health</c> → <c>bundle_adoption</c> printed <c>&lt;package&gt;: adopted only 0/N — the rest
/// compile here</c> and nothing about WHY: the seeder's per-assembly answer (identity, dependencies,
/// source fingerprint #2813, not seeded) was collapsed to covered/not before the outcome was built.
/// The outcome now carries the decline counts by reason, and <see cref="BundleAdoptionOutcome.Describe"/>
/// prints them — counts only, never type names (#4258).
/// </summary>
public class APartialAdoptionNamesItsDeclineReasonsTest
{
    private const string Registry = "https://registry.example.com";

    public static TheoryData<Seed, string> EachDeclineReason => new()
    {
        { Seed.DeclinedIdentity, "identity×1" },
        { Seed.DeclinedDependencies, "dependencies×1" },
        { Seed.DeclinedStaleSources, "fingerprint×1" },
        { Seed.DeclinedStaleSourcesCompileDispatched, "fingerprint (compiling)×1" },
        { Seed.DeclinedStaleSourcesUnservable, "fingerprint (unservable)×1" },
        { Seed.NotSeeded, "not seeded×1" },
    };

    [Theory]
    [MemberData(nameof(EachDeclineReason))]
    public void EachDeclineReason_IsNamedInDescribe(Seed declined, string expected)
    {
        var outcome = BundleOffering.AdoptedOutcome(
            "Essentials", Registry, [Seed.Adopted, declined], manifest: null);

        outcome.Adopted.Should().Be(1);
        outcome.Offered.Should().Be(2);
        outcome.IsMiss.Should().BeTrue();
        outcome.Describe().Should().Be(
            $"Essentials: adopted only 1/2 — the rest compile here; declined: {expected}");
    }

    [Fact]
    public void MixedDeclines_AreCountedByReason_LargestFirst()
    {
        var seeded = Enumerable.Repeat(Seed.DeclinedStaleSources, 7)
            .Concat(Enumerable.Repeat(Seed.DeclinedDependencies, 3))
            .ToArray();

        var outcome = BundleOffering.AdoptedOutcome("Essentials", Registry, seeded, manifest: null);

        outcome.Describe().Should().Be(
            "Essentials: adopted only 0/10 — the rest compile here; declined: fingerprint×7, dependencies×3");

        var ledger = new BundleAdoptionLedger();
        ledger.Record(outcome);
        ledger.Describe().Should().Contain("declined: fingerprint×7, dependencies×3",
            "the ledger line is what /health prints");
    }

    // ---- negative controls ----

    [Fact]
    public void CoveredOutcomes_AreNotDeclines()
    {
        var outcome = BundleOffering.AdoptedOutcome(
            "Essentials", Registry, [Seed.Adopted, Seed.AdoptedStale, Seed.AlreadyServed], manifest: null);

        outcome.IsMiss.Should().BeFalse();
        outcome.Declined.Should().BeEmpty();
        outcome.Describe().Should().Be("Essentials: adopted 3/3");
    }

    [Fact]
    public void AnOutcomeBuiltFromCountsOnly_KeepsTheOldLine()
    {
        var outcome = BundleOffering.AdoptedOutcome("Essentials", Registry, 0, 10, manifest: null);

        outcome.Describe().Should().Be("Essentials: adopted only 0/10 — the rest compile here");
    }
}

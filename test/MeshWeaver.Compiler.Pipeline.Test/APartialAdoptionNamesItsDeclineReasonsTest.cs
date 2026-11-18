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

    /// <summary>Every decline reason the seeder can answer, with the label it must render as.</summary>
    public static TheoryData<Seed, string> EachDeclineReason => new()
    {
        { Seed.DeclinedIdentity, "identity×1" },
        { Seed.DeclinedDependencies, "dependencies×1" },
        { Seed.DeclinedStaleSources, "fingerprint×1" },
        { Seed.DeclinedStaleSourcesCompileDispatched, "fingerprint (compiling)×1" },
        { Seed.DeclinedStaleSourcesUnservable, "fingerprint (unservable)×1" },
        { Seed.NotSeeded, "not seeded×1" },
    };

    /// <summary>Each decline reason, next to one adopted assembly, is named in <see cref="BundleAdoptionOutcome.Describe"/>.</summary>
    /// <param name="declined">The seeder's answer for the declined assembly.</param>
    /// <param name="expected">The label and count the line must carry.</param>
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

    /// <summary>Several reasons are counted per reason, largest first, and reach the ledger line /health prints.</summary>
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

    /// <summary>Negative control: covered outcomes are not declines and the line stays clean.</summary>
    [Fact]
    public void CoveredOutcomes_AreNotDeclines()
    {
        var outcome = BundleOffering.AdoptedOutcome(
            "Essentials", Registry, [Seed.Adopted, Seed.AdoptedStale, Seed.AlreadyServed], manifest: null);

        outcome.IsMiss.Should().BeFalse();
        outcome.Declined.Should().BeEmpty();
        outcome.Describe().Should().Be("Essentials: adopted 3/3");
    }

    /// <summary>Negative control: an outcome built from counts alone keeps the old line, with no invented reasons.</summary>
    [Fact]
    public void AnOutcomeBuiltFromCountsOnly_KeepsTheOldLine()
    {
        var outcome = BundleOffering.AdoptedOutcome("Essentials", Registry, 0, 10, manifest: null);

        outcome.Describe().Should().Be("Essentials: adopted only 0/10 — the rest compile here");
    }
}

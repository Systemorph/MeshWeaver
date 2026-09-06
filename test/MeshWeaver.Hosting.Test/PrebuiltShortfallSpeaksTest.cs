using System;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#3429 — the four answers a prebuilt-adoption shortfall must keep apart.
///
/// <para>A seeding pass that covered nothing used to say nothing, so "ten matching assemblies on
/// disk, none adopted" read in a log exactly like "no bundles mounted". Both are zero; only one is
/// a defect. <see cref="ShippedPrebuiltBundles.DescribeShortfall"/> is the pure core that turns the
/// pass's own counters into the sentence an operator acts on, and it is pinned here — with no mesh,
/// no bundle and no disk — because a reason string that drifts is exactly as unactionable as no
/// reason at all.</para>
///
/// <para>The complement, <see cref="ShippedPrebuiltBundles.IsShortfallAFailure"/>, decides the LEVEL:
/// a bundle that NAMED a requested type and still did not back it is a defect on a mesh that shipped
/// the bytes (Warning); a deployment holding types the bake never covered is a fact about the
/// deployment (Information). Getting that backwards would either bury #3429's signal or drown the
/// log in warnings about types nobody baked.</para>
/// </summary>
public class PrebuiltShortfallSpeaksTest
{
    private static readonly string[] Sources = ["/bundles", "/data/bake/sabc"];

    /// <summary>A pass that covered everything it was asked for has nothing to report — the line
    /// must never fire on the healthy path, or it becomes noise nobody reads.</summary>
    [Fact]
    public void FullCoverage_SaysNothing()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(3, 0)
        {
            SourcesConsulted = 1, SourcesPresent = 1, BundlesSeen = 1, EntriesMatched = 3,
        };

        Assert.Null(ShippedPrebuiltBundles.DescribeShortfall(tally, [], Sources));
        Assert.False(ShippedPrebuiltBundles.IsShortfallAFailure(tally));
    }

    /// <summary>ANSWER 1 — this deployment has no bundle lane at all. Nothing was lost; the line
    /// exists so an operator who EXPECTED a bake learns the mount is missing rather than inferring
    /// it from an absence.</summary>
    [Fact]
    public void NoBundleSourceOnDisk_NamesTheConfigKeys()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(0, 0) { SourcesConsulted = 2 };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(tally, ["Edu/Exercise"], Sources);

        Assert.NotNull(reason);
        Assert.Contains("No bundle source directory exists", reason, StringComparison.Ordinal);
        Assert.Contains(ShippedPrebuiltBundles.DirectoryConfigKey, reason, StringComparison.Ordinal);
        Assert.Contains(ShippedPrebuiltBundles.PublishedRootConfigKey, reason, StringComparison.Ordinal);
        Assert.Contains("Edu/Exercise", reason, StringComparison.Ordinal);
        Assert.False(ShippedPrebuiltBundles.IsShortfallAFailure(tally),
            "an absent mount is a deployment fact, not a failure of the bytes to land");
    }

    /// <summary>ANSWER 2 — the mount exists and CI published nothing for this framework identity.
    /// Distinct from answer 1 because the fix is a bake, not a volume.</summary>
    [Fact]
    public void MountedButEmpty_NamesTheIdentity()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(0, 0)
        {
            SourcesConsulted = 1, SourcesPresent = 1,
        };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(tally, ["Edu/Exercise"], Sources);

        Assert.NotNull(reason);
        Assert.Contains("hold no bundle for framework identity", reason, StringComparison.Ordinal);
        Assert.False(ShippedPrebuiltBundles.IsShortfallAFailure(tally));
    }

    /// <summary>🚨 ANSWER 3 — <b>EDU'S SHAPE.</b> Forty bundles mounted, read, identity-matching, and
    /// not one of them names a type this install wrote. The seeding pass is working perfectly and
    /// the package still compiles; the only actionable fact is that the bundles' node paths and the
    /// install's node paths do not meet, which is what the line has to say.</summary>
    [Fact]
    public void BundlesReadButNoneNamesTheseTypes_SaysTheyWereNeverCandidates()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(0, 0, FilteredOut: 40)
        {
            SourcesConsulted = 1, SourcesPresent = 1, BundlesSeen = 40, EntriesMatched = 0,
        };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(
            tally, ["Edu/Exercise", "Edu/Lesson"], Sources);

        Assert.NotNull(reason);
        Assert.Contains("40 bundle(s)", reason, StringComparison.Ordinal);
        Assert.Contains("name only NodeTypes OUTSIDE this set", reason, StringComparison.Ordinal);
        Assert.Contains("Edu/Exercise", reason, StringComparison.Ordinal);
        Assert.Contains("Edu/Lesson", reason, StringComparison.Ordinal);
        Assert.False(ShippedPrebuiltBundles.IsShortfallAFailure(tally),
            "no entry ever named these types, so nothing failed to land — the bake's coverage is "
            + "the question, and that is a fact about what was published");
    }

    /// <summary>🚨 ANSWER 4 — the bytes WERE here and did not land: an entry named the type and the
    /// adoption was declined, faulted or timed out. This is the arm that must be a Warning, and it
    /// is the one #3472 (a portal adopting bytes stamped for another framework identity) lands in
    /// — a wrong-identity adoption is worse than none, and both have to be distinguishable from
    /// success.</summary>
    [Fact]
    public void EntriesMatchedButNothingLanded_IsAFailure()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(0, 0)
        {
            SourcesConsulted = 1, SourcesPresent = 1, BundlesSeen = 3,
            EntriesMatched = 2, BundlesDeclined = 1,
        };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(tally, ["Edu/Exercise"], Sources);

        Assert.NotNull(reason);
        Assert.Contains("2 bundle entry(ies) DID name these types", reason, StringComparison.Ordinal);
        Assert.Contains("declined WHOLE on framework identity", reason, StringComparison.Ordinal);
        Assert.True(ShippedPrebuiltBundles.IsShortfallAFailure(tally),
            "bytes present and not adopted is a defect on a mesh that shipped them");
    }

    /// <summary>A partial landing is still a failure — coverage below what the entries promised
    /// means some per-type adoption was refused, and the count alone would hide it.</summary>
    [Fact]
    public void PartialLanding_IsAFailure_AndSaysHowMany()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(1, 0)
        {
            SourcesConsulted = 1, SourcesPresent = 1, BundlesSeen = 1, EntriesMatched = 3,
        };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(
            tally, ["Edu/Exercise", "Edu/Lesson"], Sources);

        Assert.NotNull(reason);
        Assert.Contains("3 bundle entry(ies) DID name these types", reason, StringComparison.Ordinal);
        Assert.Contains("and 1 landed", reason, StringComparison.Ordinal);
        Assert.True(ShippedPrebuiltBundles.IsShortfallAFailure(tally));
    }

    /// <summary>A leaving hub (#3129) deliberately runs no adoption pass. Its zero has a reason of
    /// its own and must not be mistaken for a missing mount or an uncovered bake.</summary>
    [Fact]
    public void ALeavingHub_SaysSo()
    {
        var tally = new ShippedPrebuiltBundles.SeedTally(0, 0)
        {
            SourcesConsulted = 1, Leaving = 1,
        };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(tally, ["Edu/Exercise"], Sources);

        Assert.NotNull(reason);
        Assert.Contains("LEAVING", reason, StringComparison.Ordinal);
    }

    /// <summary>The uncovered list is TRUNCATED, not dropped: a 300-type mesh must not put 300 paths
    /// on one line, and it must still say how many it left out.</summary>
    [Fact]
    public void ManyUncoveredPaths_AreTruncatedWithACount()
    {
        var uncovered = new string[12];
        for (var i = 0; i < uncovered.Length; i++)
            uncovered[i] = $"Edu/Type{i:00}";
        var tally = new ShippedPrebuiltBundles.SeedTally(0, 0) { SourcesConsulted = 1 };

        var reason = ShippedPrebuiltBundles.DescribeShortfall(tally, uncovered, Sources);

        Assert.NotNull(reason);
        Assert.Contains("Edu/Type00", reason, StringComparison.Ordinal);
        Assert.Contains("(+4 more)", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Edu/Type11", reason, StringComparison.Ordinal);
    }

    /// <summary>The tally folds across bundle sources — the image's <c>prebuilt/</c> and the
    /// CI-published root are two passes whose counters have to ADD, or a shortfall computed over
    /// one of them would describe half the evidence.</summary>
    [Fact]
    public void TalliesFoldAcrossSources()
    {
        var image = new ShippedPrebuiltBundles.SeedTally(1, 2, FilteredOut: 3)
        {
            SourcesConsulted = 1, SourcesPresent = 1, BundlesSeen = 4, EntriesMatched = 5,
            BundlesDeclined = 6, BundlesHollow = 7, Faulted = 8, Leaving = 0,
        };
        var published = new ShippedPrebuiltBundles.SeedTally(10, 20, FilteredOut: 30)
        {
            SourcesConsulted = 1, SourcesPresent = 0, BundlesSeen = 40, EntriesMatched = 50,
            BundlesDeclined = 60, BundlesHollow = 70, Faulted = 80, Leaving = 1,
        };

        var sum = image + published;

        Assert.Equal(11, sum.Adopted);
        Assert.Equal(22, sum.AlreadyCurrent);
        Assert.Equal(33, sum.Covered);
        Assert.Equal(33, sum.FilteredOut);
        Assert.Equal(2, sum.SourcesConsulted);
        Assert.Equal(1, sum.SourcesPresent);
        Assert.Equal(44, sum.BundlesSeen);
        Assert.Equal(55, sum.EntriesMatched);
        Assert.Equal(66, sum.BundlesDeclined);
        Assert.Equal(77, sum.BundlesHollow);
        Assert.Equal(88, sum.Faulted);
        Assert.Equal(1, sum.Leaving);
    }
}

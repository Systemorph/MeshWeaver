#pragma warning disable CS1591

using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// <see cref="GateShardPlan"/> — the fan-out that took the node-repo gate off its 30-minute cap
/// (the portal/nodeops tax of MeshWeaver#2543, paid once per install). Two properties have to hold,
/// and each has a way of failing SILENTLY, which is why they are asserted rather than argued:
///
/// <list type="number">
/// <item>the slices are a DISJOINT COVER of the discovered set — a package assigned to no shard is
/// a package nobody gated, and the aggregate job's receipts check is what catches it in CI, but it
/// can only catch what the plan actually emits;</item>
/// <item>every shard MOUNTS the forward dependency closure of its own slice — a shard missing a
/// dependency fails its installs and reports a CONTENT-shaped error with a sharding cause.</item>
/// </list>
/// </summary>
public class GateShardPlanTest
{
    private static PackageManifest Package(string id, params string[] requires) =>
        new()
        {
            Id = id,
            Name = id,
            Kind = PackageKind.NodeRepo,
            TargetPartition = id,
            SourceFolder = id,
            Version = "sha",
            Requires = requires.ToImmutableList(),
        };

    private static readonly RepoSnapshot NoFiles = new("sha", []);

    private static GateShardPlan.Assignment Assign(
        IReadOnlyList<PackageManifest> packages, int index, int total)
    {
        var ordered = LocalNodeRepo.OrderByDependencies(packages, NoFiles);
        return GateShardPlan.Assign(
            ordered, LocalNodeRepo.DependencyMap(packages, NoFiles), new GateShard(index, total));
    }

    // ── the parse: a malformed shard must be refused, never silently read as "everything" ──

    [Theory]
    [InlineData("2/4", 2, 4)]
    [InlineData("1/1", 1, 1)]
    public void Parse_AcceptsTheOneBasedForm(string value, int index, int total)
    {
        var (shard, problem) = GateShard.Parse(value);
        Assert.Null(problem);
        Assert.Equal(new GateShard(index, total), shard);
    }

    [Theory]
    [InlineData("4")]        // no total — would otherwise read as shard 4 of everything
    [InlineData("0/4")]      // 0-based off-by-one: the whole first slice would go ungated
    [InlineData("5/4")]      // past the end: an empty slice that looks like a shard doing its share
    [InlineData("2/0")]      // no shards at all
    [InlineData("a/b")]
    public void Parse_RefusesAnythingElse(string value)
    {
        var (shard, problem) = GateShard.Parse(value);
        Assert.Null(shard);
        Assert.NotNull(problem);
    }

    // ── the cover ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPackageIsGatedByExactlyOneShard()
    {
        var packages = Enumerable.Range(0, 17).Select(i => Package($"P{i:D2}")).ToArray();
        var gated = Enumerable.Range(1, 4)
            .SelectMany(i => Assign(packages, i, 4).Gated.Select(p => p.Id))
            .ToList();

        Assert.Equal(packages.Length, gated.Count);          // no package gated twice
        Assert.Equal(
            packages.Select(p => p.Id).OrderBy(id => id),
            gated.OrderBy(id => id));                        // and none left out
    }

    [Fact]
    public void ShardsAreBalancedToWithinOnePackage()
    {
        // Equal COUNTS is the whole weighting policy — measured on MeshWeaver.Plugins run
        // 33758875713, it lands within 0.4 min of a perfect oracle at every shard count from 2 to
        // 6, while every structural proxy (files, NodeType count, test bytes) loses to it. So the
        // one thing to assert is that the counts really are equal.
        var packages = Enumerable.Range(0, 17).Select(i => Package($"P{i:D2}")).ToArray();
        var counts = Enumerable.Range(1, 4).Select(i => Assign(packages, i, 4).Gated.Count).ToList();
        Assert.True(counts.Max() - counts.Min() <= 1, $"counts were {string.Join(",", counts)}");
    }

    [Fact]
    public void MoreShardsThanPackagesLeavesTheExtraShardsEmpty_NotDuplicated()
    {
        var packages = new[] { Package("A"), Package("B") };
        Assert.Equal(["A"], Assign(packages, 1, 4).Gated.Select(p => p.Id));
        Assert.Equal(["B"], Assign(packages, 2, 4).Gated.Select(p => p.Id));
        Assert.Empty(Assign(packages, 3, 4).Gated);
        Assert.Empty(Assign(packages, 4, 4).Gated);
    }

    // ── the closure ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AShardMountsItsSlicesDependencies_ButDoesNotGateThem()
    {
        // Order: Base → Leaf (the requires edge). With two shards, position 0 (Base) is shard 1's
        // and position 1 (Leaf) is shard 2's — so shard 2 must MOUNT Base without gating it.
        var packages = new[] { Package("Leaf", "Base"), Package("Base") };
        var shard = Assign(packages, 2, 2);

        Assert.Equal(["Leaf"], shard.Gated.Select(p => p.Id));
        Assert.Equal(["Base"], shard.Support.Select(p => p.Id));
        Assert.Equal(["Base", "Leaf"], shard.Installed.Select(p => p.Id));
    }

    [Fact]
    public void TheClosureIsTransitive()
    {
        // Top → Middle → Bottom. A shard that mounted only the DIRECT dependency would fail its
        // install one level down, and the error would name the content rather than the sharding.
        var packages = new[]
        {
            Package("Top", "Middle"), Package("Middle", "Bottom"), Package("Bottom"),
        };
        var shard = Assign(packages, 3, 3);   // position 2 in Bottom → Middle → Top is "Top"

        Assert.Equal(["Top"], shard.Gated.Select(p => p.Id));
        Assert.Equal(["Bottom", "Middle"], shard.Support.Select(p => p.Id));
    }

    [Fact]
    public void TheInstallListKeepsTheRunsDependencyOrder()
    {
        // 🚨 Never the slice's own order and never alphabetical: a support package has to land
        // BEFORE the package that needs it, or the install is refused with the dependency's type
        // "not registered".
        var packages = new[] { Package("Aaa", "Zzz"), Package("Zzz") };
        var shard = Assign(packages, 1, 1);
        Assert.Equal(["Zzz", "Aaa"], shard.Installed.Select(p => p.Id));
    }

    [Fact]
    public void OneShardOfOneGatesEverythingAndSupportsNothing()
    {
        var packages = new[] { Package("Leaf", "Base"), Package("Base") };
        var shard = Assign(packages, 1, 1);
        Assert.Equal(["Base", "Leaf"], shard.Gated.Select(p => p.Id));
        Assert.Empty(shard.Support);
    }

    // ── the receipt ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DescribeNamesTheDiscoveredTotalAndBothSets()
    {
        // The aggregate job parses this line from every shard's log and refuses a run whose slices
        // do not cover the discovered set. It therefore has to carry the TOTAL (so a shard that
        // discovered a different tree is detectable) and the slice by NAME.
        var packages = new[] { Package("Leaf", "Base"), Package("Base") };
        var line = GateShardPlan.Describe(new GateShard(2, 2), 2, Assign(packages, 2, 2));

        Assert.Contains("shard 2/2", line);
        Assert.Contains("gating 1 of 2 discovered package(s)", line);
        Assert.Contains("Leaf", line);
        Assert.Contains("Base", line);
    }
}

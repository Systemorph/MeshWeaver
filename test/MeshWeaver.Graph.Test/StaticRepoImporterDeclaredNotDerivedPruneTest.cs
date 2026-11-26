using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Policy <c>declared-not-derived</c> applied to the static-repo import prune. A pearl boot of the
/// first 4.x pod ran the import before <c>MeshWeaver.AI</c> landed: the <c>Provider</c> source was
/// absent, its marker made it an "orphan", and the reconcile deleted the whole partition
/// (1,491 nodes, <c>_Access</c> grants included). An absent contributor is NOT a removal; pruning
/// removes only what the declaration itself dropped.
/// </summary>
public class StaticRepoImporterDeclaredNotDerivedPruneTest
{
    // (a) module absent -> nothing is pruned (cannot judge).
    [Fact]
    public void ModuleAbsent_ProviderSourceNotRegistered_PrunesNothing()
    {
        var orphans = StaticRepoImporter.ComputeOrphanedSourcePartitions(
            previouslyOwned: ["Doc", "Agent", "Provider"],
            currentSources: ["Doc", "Agent"]);
        orphans.Should().BeEquivalentTo(new[] { "Provider" }, JsonSerializerOptions.Default,
            because: "the orphan DETECTION still sees it - the verdict is what must refuse");

        var verdict = StaticRepoImporter.JudgeOrphanedSourcePartitions(
            orphans, StaticRepoImporter.BuiltInRetiredPartitions);

        verdict.Retire.Should().BeEmpty("an absent contributor is not a removal");
        verdict.CannotJudge.Should().BeEquivalentTo(new[] { "Provider" }, JsonSerializerOptions.Default);
    }

    // Negative control: a DECLARED retirement still retires.
    [Fact]
    public void DeclaredRetirement_StillRetires_AndOnlyThatPartition()
    {
        var verdict = StaticRepoImporter.JudgeOrphanedSourcePartitions(
            orphans: ["Command", "Provider"],
            declaredRetired: StaticRepoImporter.BuiltInRetiredPartitions);

        verdict.Retire.Should().BeEquivalentTo(new[] { "Command" }, JsonSerializerOptions.Default);
        verdict.CannotJudge.Should().BeEquivalentTo(new[] { "Provider" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void DeclaredRetirement_IsCaseInsensitive()
    {
        StaticRepoImporter.JudgeOrphanedSourcePartitions(["COMMAND"], ["command"])
            .Retire.Should().BeEquivalentTo(new[] { "COMMAND" }, JsonSerializerOptions.Default);
    }

    private static MeshNode Node(string path)
    {
        var segs = path.Split('/');
        return new MeshNode(segs[^1], string.Join('/', segs[..^1]))
            { SyncBehavior = SyncBehavior.Include, State = MeshNodeState.Active };
    }

    // (b) a declaration drop prunes exactly that path.
    [Fact]
    public void DeclarationDrop_PrunesExactlyThePathDropped()
    {
        var pruned = StaticRepoImporter.ComputePrunableNodes(
                existing: [Node("Provider/A"), Node("Provider/B"), Node("Provider/Runtime")],
                sourcePaths: ["Provider/A"],
                previouslyOwnedPaths: ["Provider/A", "Provider/B"],
                excludedRoots: [], PartitionSyncMode.FullReplace)
            .Select(n => n.Path);

        pruned.Should().BeEquivalentTo(new[] { "Provider/B" }, JsonSerializerOptions.Default,
            because: "B was in the previous manifest and is absent from the new one; Runtime was never "
                     + "owned and A is still declared");
    }

    // (c) _Access / _Policy survive, even if a manifest wrongly lists them.
    [Fact]
    public void AccessAndPolicySatellites_SurviveAPrune()
    {
        var pruned = StaticRepoImporter.ComputePrunableNodes(
                existing:
                [
                    Node("Provider/A"), Node("Provider/_Access/alice_Access"),
                    Node("Provider/_Policy"), Node("Provider/_Access"),
                ],
                sourcePaths: [],
                previouslyOwnedPaths:
                    ["Provider/A", "Provider/_Access/alice_Access", "Provider/_Policy", "Provider/_Access"],
                excludedRoots: [], PartitionSyncMode.FullReplace)
            .Select(n => n.Path);

        pruned.Should().BeEquivalentTo(new[] { "Provider/A" }, JsonSerializerOptions.Default);
    }

    // (d) the bound refuses a mass delete.
    [Fact]
    public void Bound_RefusesAMassDelete()
    {
        StaticRepoImporter.ExceedsPruneBound(candidateCount: 1491, partitionSize: 1524)
            .Should().BeTrue("deleting 98% of a partition in one prune is a misjudgement");
        StaticRepoImporter.ExceedsPruneBound(candidateCount: 40, partitionSize: 100)
            .Should().BeTrue();
    }

    // Negative control: a legitimate single removal (or a small one) is never bounded.
    [Theory]
    [InlineData(1, 1500)]
    [InlineData(1, 1)]      // a one-node partition losing its one node
    [InlineData(10, 12)]    // at/below the floor, whatever the fraction
    [InlineData(25, 100)]   // exactly the fraction
    public void Bound_LetsALegitimateRemovalThrough(int candidates, int total)
    {
        StaticRepoImporter.ExceedsPruneBound(candidates, total).Should().BeFalse();
    }

    [Fact]
    public void LegitimateSingleRemoval_StillPrunesThroughTheWholeDecision()
    {
        var existing = Enumerable.Range(0, 200).Select(i => Node($"Doc/N{i}")).ToArray();
        var owned = existing.Select(n => n.Path).ToArray();
        var source = owned.Where(p => p != "Doc/N7").ToArray();

        var pruned = StaticRepoImporter.ComputePrunableNodes(
            existing, source, owned, excludedRoots: [], PartitionSyncMode.FullReplace);

        pruned.Select(n => n.Path).Should().BeEquivalentTo(new[] { "Doc/N7" }, JsonSerializerOptions.Default);
        StaticRepoImporter.ExceedsPruneBound(pruned.Count, existing.Length).Should().BeFalse();
    }
}

using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the per-partition prune DECISION under each <see cref="PartitionSyncMode"/>
/// (<see cref="StaticRepoImporter.ComputePrunableNodes"/>) — the "what does an import remove" logic.
/// Pure + deterministic (no database), mirroring <see cref="StaticRepoImporterPruneTest"/>:
/// <list type="bullet">
///   <item><b>Additive</b> — a user-added node (never in a manifest) SURVIVES re-import; a node the
///     source PREVIOUSLY shipped but has since dropped IS pruned.</item>
///   <item><b>FullReplace</b> (default) — every extra absent from the source is pruned (unchanged
///     behavior, incl. a user-added node), so non-opted-in partitions do not regress.</item>
///   <item><b>UpsertOnly</b> — nothing is ever pruned.</item>
/// </list>
/// The per-node <see cref="SyncBehavior"/> guard (claimed nodes) and governance/excluded-root guards
/// hold in EVERY mode.
/// </summary>
public class StaticRepoImporterSyncModeTest
{
    private const string Partition = "AI";

    // A synced source node the build still ships this run.
    private static MeshNode Shipped => Node("Shipped1");
    // A node the source shipped LAST run (so it's in the previous manifest) but has since dropped.
    private static MeshNode Removed => Node("Removed1");
    // A node a user created directly in the partition — never present in any manifest.
    private static MeshNode UserAdded => Node("UserAdded");
    // Governance satellite (a "_"-segment after the root) — never pruned in any mode.
    private static MeshNode Governance => Node("_Policy");
    // A node the user CLAIMED (sync: none) — never pruned in any mode.
    private static MeshNode Claimed => Node("Claimed", SyncBehavior.ExcludeThisAndChildren);

    private static MeshNode Node(string id, SyncBehavior sync = SyncBehavior.Include) =>
        new(id, Partition) { SyncBehavior = sync, State = MeshNodeState.Active };

    // The source this run ships ONLY Shipped1. Last run it owned Shipped1 + Removed1 (the manifest).
    private static readonly string[] CurrentSourcePaths = ["AI/Shipped1"];
    private static readonly string[] PreviousManifestPaths = ["AI/Shipped1", "AI/Removed1"];

    private static string[] Prune(PartitionSyncMode mode, params MeshNode[] existing) =>
        StaticRepoImporter.ComputePrunableNodes(
                existing, CurrentSourcePaths, PreviousManifestPaths, excludedRoots: [], mode)
            .Select(n => n.Path)
            .ToArray();

    [Fact]
    public void Additive_UserAddedNode_Survives_And_RemovedSourceNode_IsPruned()
    {
        var pruned = Prune(PartitionSyncMode.Additive, Shipped, Removed, UserAdded);

        // Only the previously-shipped-but-now-dropped node is pruned; the user's own node survives.
        pruned.Should().BeEquivalentTo(new[] { "AI/Removed1" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void FullReplace_PrunesEveryExtra_IncludingUserAddedNode()
    {
        var pruned = Prune(PartitionSyncMode.FullReplace, Shipped, Removed, UserAdded);

        // Mirror behavior (the default for non-opted-in partitions): every node absent from the source
        // is pruned — including a user-added one. This is the behavior we must NOT regress.
        pruned.Should().BeEquivalentTo(new[] { "AI/Removed1", "AI/UserAdded" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void UpsertOnly_PrunesNothing()
    {
        Prune(PartitionSyncMode.UpsertOnly, Shipped, Removed, UserAdded).Should().BeEmpty();
    }

    [Fact]
    public void Additive_FirstImport_EmptyManifest_PrunesNothing()
    {
        // No previous manifest (first-ever import): additive can't know what the source "previously
        // owned", so it prunes nothing — a pre-existing node (even one absent from the source) is kept.
        var pruned = StaticRepoImporter.ComputePrunableNodes(
                new[] { Shipped, Removed, UserAdded }, CurrentSourcePaths,
                previouslyOwnedPaths: [], excludedRoots: [], PartitionSyncMode.Additive)
            .Select(n => n.Path)
            .ToArray();

        pruned.Should().BeEmpty();
    }

    [Fact]
    public void Governance_And_ClaimedNodes_AreNeverPruned_InAnyMode()
    {
        // Governance (_Policy) and a claimed (ExcludeThisAndChildren) node are absent from the source
        // AND were "previously owned" — yet the guards keep them out of the prune set in every mode.
        foreach (var mode in new[]
                 {
                     PartitionSyncMode.FullReplace, PartitionSyncMode.Additive, PartitionSyncMode.UpsertOnly
                 })
        {
            var pruned = StaticRepoImporter.ComputePrunableNodes(
                    new[] { Governance, Claimed },
                    CurrentSourcePaths,
                    // Pretend both were previously owned so ONLY the guards can protect them.
                    previouslyOwnedPaths: ["AI/_Policy", "AI/Claimed"],
                    excludedRoots: [],
                    mode)
                .Select(n => n.Path)
                .ToArray();

            pruned.Should().BeEmpty($"governance + claimed nodes must never be pruned ({mode})");
        }
    }

    [Fact]
    public void NodeUnderExcludedRoot_IsNeverPruned()
    {
        // A node at/under a claimed root subtree is protected even when it's a source-owned orphan.
        var underClaimed = new MeshNode("child", "AI/Sub") { State = MeshNodeState.Active };

        var pruned = StaticRepoImporter.ComputePrunableNodes(
                new[] { underClaimed },
                CurrentSourcePaths,
                previouslyOwnedPaths: ["AI/Sub/child"],
                excludedRoots: ["AI/Sub"],
                PartitionSyncMode.FullReplace)
            .Select(n => n.Path)
            .ToArray();

        pruned.Should().BeEmpty();
    }
}

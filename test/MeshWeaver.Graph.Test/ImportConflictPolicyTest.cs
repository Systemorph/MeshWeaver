using System;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for the two-way import conflict DECISION
/// (<see cref="ImportConflictPolicy.PreservesServerCopyOf"/>) — "is this live node a local edit made
/// since the last sync that must be preserved rather than overwritten?". Pure + deterministic — no
/// database. The git-first default never preserves; two-way preserves a node newer than the sync
/// baseline; force always overwrites.
/// </summary>
public class ImportConflictPolicyTest
{
    private static readonly DateTimeOffset LastSync = new(2026, 07, 14, 00, 00, 00, TimeSpan.Zero);

    /// <summary>A node a PERSON edited on the portal at <paramref name="when"/> — the edit the
    /// two-way protection exists for. Every write a person makes stamps their id.</summary>
    private static MeshNode NodeModifiedAt(DateTimeOffset when) =>
        MeshNode.FromPath("Space/node") with { LastModified = when, LastModifiedBy = "alice" };

    /// <summary>A node the IMPORT itself wrote at <paramref name="when"/>: the system identity, or
    /// no author at all (imports land under <c>RunAsSystem</c>; a node read back from storage
    /// carries whichever the write stamped).</summary>
    private static MeshNode ImportWrittenAt(DateTimeOffset when, string? author) =>
        MeshNode.FromPath("Space/node") with { LastModified = when, LastModifiedBy = author };

    [Fact]
    public void GitFirst_NeverPreserves()
    {
        var target = NodeModifiedAt(LastSync.AddHours(1)); // newer on server, but git-first
        ImportConflictPolicy.GitFirst.PreservesServerCopyOf(target).Should().BeFalse();
    }

    [Fact]
    public void TwoWay_PreservesNodeChangedAfterLastSync()
    {
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: LastSync);
        policy.PreservesServerCopyOf(NodeModifiedAt(LastSync.AddSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public void TwoWay_DoesNotPreserveNodeUnchangedSinceLastSync()
    {
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: LastSync);
        // Not touched on the server since the last sync → the repo is free to update it.
        policy.PreservesServerCopyOf(NodeModifiedAt(LastSync.AddSeconds(-1))).Should().BeFalse();
    }

    [Fact]
    public void Force_OverridesTwoWay_NeverPreserves()
    {
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: LastSync, Force: true);
        policy.PreservesServerCopyOf(NodeModifiedAt(LastSync.AddHours(1))).Should().BeFalse();
    }

    [Fact]
    public void TwoWay_WithNoSyncBaseline_DoesNotPreserve()
    {
        // No LastSyncedAt recorded yet (first sync) → nothing to protect; stays git-first.
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: null);
        policy.PreservesServerCopyOf(NodeModifiedAt(LastSync.AddHours(1))).Should().BeFalse();
    }

    [Fact]
    public void TwoWay_GitOnlyNode_NotPreserved()
    {
        // A node present only in the repo (no live target) is a new addition to import, not a local edit.
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: LastSync);
        policy.PreservesServerCopyOf(null).Should().BeFalse();
    }

    // ── Only a HUMAN's write is a server edit (memex.systemorph.com, 2026-09-08) ────────────
    //
    // The horizon (LastSyncedAt) is held while ANY node is preserved. An import's own writes land
    // under the system identity with the import's clock — later than a held horizon — so judged
    // by timestamp alone they read as "newer on the server" at the NEXT import and are preserved
    // from the prune. Three Crm/Source/Mail* files a commit had deleted therefore never left the
    // instance, and no bake of the repository could match its sources again. Authorship is what
    // separates a person's uncommitted edit (protected) from the previous import's leftovers
    // (the repo's deletion applies).

    [Fact]
    public void TwoWay_DoesNotPreserveTheImportsOwnWrite_FromOverwriteOrPrune()
    {
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: LastSync);
        foreach (var author in new[] { null, "", WellKnownUsers.System })
        {
            var leftover = ImportWrittenAt(LastSync.AddHours(1), author);
            policy.PreservesServerCopyOf(leftover).Should().BeFalse(
                $"a node the import wrote (author '{author}') is not a person's edit, however new its clock");
            policy.PreservesFromPruneOf(leftover).Should().BeFalse(
                $"a file the repo deleted must leave the mesh even when the horizon is held (author '{author}')");
        }
    }

    [Fact]
    public void BidirectionalAdditions_DoNotKeepTheImportsOwnWriteFromThePrune()
    {
        var policy = new ImportConflictPolicy(
            PreserveServerNewer: false, Since: LastSync, PreserveServerAdditions: true);
        policy.PreservesFromPruneOf(ImportWrittenAt(LastSync.AddHours(1), WellKnownUsers.System)).Should().BeFalse();
        policy.PreservesFromPruneOf(NodeModifiedAt(LastSync.AddHours(1))).Should().BeTrue(
            "the control arm: the same clock with a person's id IS protected");
    }

    [Fact]
    public void IsHumanEdit_IsTheAuthorshipRule()
    {
        ImportConflictPolicy.IsHumanEdit(NodeModifiedAt(LastSync)).Should().BeTrue();
        ImportConflictPolicy.IsHumanEdit(ImportWrittenAt(LastSync, null)).Should().BeFalse();
        ImportConflictPolicy.IsHumanEdit(ImportWrittenAt(LastSync, WellKnownUsers.System)).Should().BeFalse();
        ImportConflictPolicy.IsHumanEdit(null).Should().BeFalse();
    }

    // ── PreservesFromPruneOf — the PRUNE-protection decision (issues #604/#677) ──────────────

    [Fact]
    public void GitFirst_NeverPreservesFromPrune()
    {
        // A one-directional mirror keeps FullReplace semantics: extras are pruned regardless of age.
        ImportConflictPolicy.GitFirst.PreservesFromPruneOf(NodeModifiedAt(LastSync.AddHours(1)))
            .Should().BeFalse();
    }

    [Fact]
    public void TwoWay_PreservesServerNewerFromPrune()
    {
        // Full two-way protection covers the prune exactly like the overwrite (#675).
        var policy = new ImportConflictPolicy(PreserveServerNewer: true, Since: LastSync);
        policy.PreservesFromPruneOf(NodeModifiedAt(LastSync.AddSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public void BidirectionalAdditions_PreservesFromPrune_ButNotFromOverwrite()
    {
        // The #604 shape: a bidirectional Space with two-way OFF. A server-side addition made since
        // the last sync must survive the prune (the mesh is an editing surface, not a mirror) —
        // while overwrite conflicts stay git-first (the repo still wins on a changed file).
        var policy = new ImportConflictPolicy(
            PreserveServerNewer: false, Since: LastSync, PreserveServerAdditions: true);
        var serverAddition = NodeModifiedAt(LastSync.AddSeconds(1));
        policy.PreservesFromPruneOf(serverAddition).Should().BeTrue();
        policy.PreservesServerCopyOf(serverAddition).Should().BeFalse();
    }

    [Fact]
    public void BidirectionalAdditions_NodeUnchangedSinceLastSync_IsPruned()
    {
        // A node the last sync already reconciled (older than the horizon) whose repo file is gone
        // is a genuine repo-side deletion — the mirror prune applies.
        var policy = new ImportConflictPolicy(
            PreserveServerNewer: false, Since: LastSync, PreserveServerAdditions: true);
        policy.PreservesFromPruneOf(NodeModifiedAt(LastSync.AddSeconds(-1))).Should().BeFalse();
    }

    [Fact]
    public void Force_OverridesPruneProtection()
    {
        // The deliberate-discard escape hatch prunes regardless of protection.
        var policy = new ImportConflictPolicy(
            PreserveServerNewer: true, Since: LastSync, Force: true, PreserveServerAdditions: true);
        policy.PreservesFromPruneOf(NodeModifiedAt(LastSync.AddHours(1))).Should().BeFalse();
    }

    [Fact]
    public void PruneProtection_WithNoSyncBaseline_DoesNotPreserve()
    {
        // No recorded horizon (first sync) → nothing to protect; the mirror prune applies.
        var policy = new ImportConflictPolicy(
            PreserveServerNewer: false, Since: null, PreserveServerAdditions: true);
        policy.PreservesFromPruneOf(NodeModifiedAt(LastSync.AddHours(1))).Should().BeFalse();
    }
}

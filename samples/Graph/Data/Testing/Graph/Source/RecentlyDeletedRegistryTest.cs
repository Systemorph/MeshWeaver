// <meshweaver>
// Id: Testing/Graph/RecentlyDeletedRegistryTest
// DisplayName: Testing/Graph/RecentlyDeletedRegistryTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;

/// <summary>
/// Deterministic unit guard for <see cref="RecentlyDeletedRegistry"/> — the mesh-scoped
/// "delete wins" tombstone that stops a per-node hub which (re)activates after a delete from
/// resurrecting the row via its activation-save. (The end-to-end resurrection race itself is
/// pinned by <c>SpaceDeletionPartitionDropTests.DeletingSpace_DropsWholePartition_AndSameIdCanBeRecreated</c>'s
/// authoritative-storage resurrection guard; this covers the mechanism's contract in isolation.)
/// </summary>
public class RecentlyDeletedRegistryTest
{
    [MeshFact(TimeoutSeconds = 5)]
    public void MarkDeleted_ThenIsRecentlyDeleted_IsTrue()
    {
        var registry = new RecentlyDeletedRegistry();
        registry.IsRecentlyDeleted("Admin/Partition/space1").Should().BeFalse(
            "an untracked path was never deleted");

        registry.MarkDeleted("Admin/Partition/space1");

        registry.IsRecentlyDeleted("Admin/Partition/space1").Should().BeTrue(
            "the path was just marked deleted, so a resurrecting write must be dropped");
        registry.IsRecentlyDeleted("Admin/Partition/other").Should().BeFalse(
            "only the marked path is tombstoned");
    }

    [MeshFact(TimeoutSeconds = 5)]
    public void Clear_LiftsTheTombstone_SoARecreatePersists()
    {
        var registry = new RecentlyDeletedRegistry();
        registry.MarkDeleted("Admin/Partition/space1");
        registry.IsRecentlyDeleted("Admin/Partition/space1").Should().BeTrue();

        // A legitimate re-create (same id) clears the tombstone so its writes are NOT dropped.
        registry.Clear("Admin/Partition/space1");

        registry.IsRecentlyDeleted("Admin/Partition/space1").Should().BeFalse(
            "after a re-create the path must persist normally again");
    }

    [MeshFact(TimeoutSeconds = 5)]
    public void IsCaseInsensitive_And_NullSafe()
    {
        var registry = new RecentlyDeletedRegistry();
        registry.MarkDeleted("Admin/Partition/Space1");

        registry.IsRecentlyDeleted("admin/partition/space1").Should().BeTrue(
            "paths compare case-insensitively (partition schemas are lowercased)");

        // Null / empty must never throw and are never "recently deleted".
        registry.IsRecentlyDeleted(null).Should().BeFalse();
        registry.IsRecentlyDeleted("").Should().BeFalse();
        registry.MarkDeleted(null);
        registry.Clear(null);
    }
}

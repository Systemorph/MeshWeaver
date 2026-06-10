#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Repro + guard for the 2026-06-10 <b>"ThreadComposer disappears on model-select"</b> bug.
///
/// <para>The composer is a per-user singleton at <c>{user}/_Thread/ThreadComposer</c>. For the
/// single-node read/write to resolve the SAME Postgres table, <c>ThreadComposer</c> must map to
/// the <c>threads</c> table by BOTH its path segment (<c>_Thread</c>) AND its nodeType. When it was
/// missing from the nodeType→table map, the write routed to <c>threads</c> (by path) while the
/// nodeType resolved to <c>mesh_nodes</c> → the single-node read missed the row → routing
/// <c>NotFound</c> → the composer's bound <c>SynchronizationStream</c> OnErrored and the input box
/// vanished. Registering <c>ThreadComposer</c> on the <c>_Thread</c>/<c>threads</c>
/// <see cref="SatelliteTableMapping"/> makes both resolutions agree.</para>
///
/// <para>Deterministic + DB-free: it exercises the routing CONTRACT
/// (<see cref="SatelliteTableMapping"/>) the Postgres adapter resolves against, so it pins the
/// defect without a live database.</para>
/// </summary>
public class ThreadComposerRoutingTest
{
    private static readonly Dictionary<string, string> SegmentTable =
        SatelliteTableMapping.ToSegmentTableMap(SatelliteTableMapping.Defaults);

    private static readonly Dictionary<string, string> NodeTypeTable =
        SatelliteTableMapping.ToNodeTypeTableMap(SatelliteTableMapping.Defaults);

    // Mirrors the storage adapter's single-node table resolution: the first path segment that is a
    // registered satellite segment wins; otherwise the main `mesh_nodes` table.
    private static string TableForPath(string path) =>
        path.Split('/')
            .Select(seg => SegmentTable.TryGetValue(seg, out var t) ? t : null)
            .FirstOrDefault(t => t is not null) ?? "mesh_nodes";

    private static string TableForNodeType(string nodeType) =>
        NodeTypeTable.TryGetValue(nodeType, out var t) ? t : "mesh_nodes";

    [Fact]
    public void PerUserComposer_PathAndNodeType_ResolveToTheSameTable()
    {
        var path = ThreadComposerNodeType.PathFor("rbuergi"); // rbuergi/_Thread/ThreadComposer

        var byPath = TableForPath(path);
        var byNodeType = TableForNodeType(ThreadComposerNodeType.NodeType);

        Assert.Equal("threads", byPath); // the _Thread segment routes to the threads satellite table

        // THE INVARIANT: write (path-resolved) and single-node read (nodeType-resolved) must land
        // in the SAME table, or the row is unreadable → NotFound → the composer's layout-area
        // stream OnErrors and the box disappears.
        Assert.Equal(byPath, byNodeType);
    }

    [Fact]
    public void PerNodeComposer_PathAndNodeType_ResolveToTheSameTable()
    {
        var path = ThreadComposerNodeType.PathForNode("Acme/Spaces/Sales", "rbuergi");

        Assert.Equal(TableForPath(path), TableForNodeType(ThreadComposerNodeType.NodeType));
        Assert.Equal("threads", TableForNodeType(ThreadComposerNodeType.NodeType));
    }
}

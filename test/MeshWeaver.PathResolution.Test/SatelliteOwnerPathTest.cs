using System.Linq;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.PathResolution.Test;

/// <summary>
/// Pins <see cref="SatelliteTableMapping.OwnerOfSatellitePath"/> — the derivation behind a
/// satellite's <see cref="MeshNode.MainNode"/>. MainNode must be a REAL main node: permissions
/// delegate to it (<c>SatelliteAccessRule</c>), an AccessAssignment's grant projects at prefix
/// <c>COALESCE(main_node, namespace)</c>, and the access-granted notification names and links it.
/// Stamping the satellite CONTAINER (<c>{owner}/_Access</c>) instead broke all three.
/// </summary>
public class SatelliteOwnerPathTest
{
    [Theory]
    // The container itself (what the create handler stamps from — the node's namespace).
    [InlineData("Space/_Access", "Space")]
    [InlineData("Space/Docs/Report/_Access", "Space/Docs/Report")]
    [InlineData("Doc/_Thread", "Doc")]
    // A full satellite instance path.
    [InlineData("Space/_Access/alice_Access", "Space")]
    [InlineData("Doc/_Thread/hello-world", "Doc")]
    // Nested satellites cut at the FIRST segment — the owner is the last real node, and a MainNode
    // pointing at another satellite is the "Access denied" shape migration V08 had to repair.
    [InlineData("Doc/_Thread/t1/_ThreadMessage/m1", "Doc")]
    [InlineData("Space/_Access/a1/_Activity/log1", "Space")]
    // No satellite segment → the path IS a main node (the legacy `{scope}/{subject}_Access` shape).
    [InlineData("TestOrg", "TestOrg")]
    [InlineData("Space/Docs/Report", "Space/Docs/Report")]
    // Root-level satellite: no owner — the documented MainNode of a root-scope grant.
    [InlineData("_Access", "")]
    [InlineData("_Access/rbuergi_Access", "")]
    // Only the EXACT satellite segments count: `_Policy`/`_AccessLog` are ordinary mesh_nodes rows,
    // and `Source`/`Test` are primary content that shares the code table.
    [InlineData("Space/_Policy", "Space/_Policy")]
    [InlineData("Space/_AccessLog", "Space/_AccessLog")]
    [InlineData("MyType/Source", "MyType/Source")]
    // Case-sensitive, like the storage router's segment matching.
    [InlineData("Space/_access", "Space/_access")]
    [InlineData("", "")]
    public void OwnerOfSatellitePath_CutsAtTheFirstSatelliteSegment(string path, string expected)
        => Assert.Equal(expected, SatelliteTableMapping.OwnerOfSatellitePath(path));

    [Fact]
    public void OwnerOfSatellitePath_NullIsEmpty()
        => Assert.Equal("", SatelliteTableMapping.OwnerOfSatellitePath(null));

    [Fact]
    public void OwnerOfSatellitePath_NeverReturnsASatellitePath()
    {
        // The invariant that matters downstream: whatever comes back can be permission-checked and
        // linked as a node. (An owner that is itself a satellite path is the V08 defect shape.)
        foreach (var path in new[]
                 {
                     "Space/_Access/alice_Access", "Doc/_Thread/t1/_ThreadMessage/m1",
                     "Space/_Access/a1/_Activity/log1", "Doc/_Comment/c1",
                 })
            Assert.False(SatelliteTableMapping.IsSatellitePath(
                SatelliteTableMapping.OwnerOfSatellitePath(path)));
    }

    // ── IsSatelliteId — the PARENTAGE question, #2383 ───────────────────────────────────────────

    /// <summary>
    /// <see cref="SatelliteTableMapping.IsSatelliteId"/> classifies a node's OWN id as a sibling
    /// satellite. It is what decides whether the create/upsert handler derives MainNode instead of
    /// leaving the record's self-default in place, so the boundary cases are load-bearing.
    /// </summary>
    [Theory]
    // The sibling satellites that share mesh_nodes — the population #2383 is about.
    [InlineData("_Policy", true)]
    [InlineData("_Provider", true)]
    [InlineData("_GitSync", true)]
    [InlineData("_DefaultInstallLedger", true)]
    [InlineData("_Entitlements", true)]
    // The enumerated table segments are satellite-shaped too, so one rule covers both families.
    [InlineData("_Access", true)]
    [InlineData("_Thread", true)]
    [InlineData("_Comment", true)]
    // Ordinary content is never a satellite, whatever its case.
    [InlineData("Readme", false)]
    [InlineData("Source", false)]
    [InlineData("alice_Access", false)]
    // 🚨 Underscore + LOWER-case is NOT a satellite. Authored content may legitimately be named
    // `_index` / `_draft`, and re-pointing its MainNode would drop it out of every `is:main`
    // listing and out of search_across_schemas (`WHERE main_node = path`) — i.e. hide real content.
    [InlineData("_index", false)]
    [InlineData("_draft", false)]
    // Degenerate ids: a bare underscore has no type name after it.
    [InlineData("_", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSatelliteId_MatchesUnderscoreUpperOnly(string? id, bool expected)
        => Assert.Equal(expected, SatelliteTableMapping.IsSatelliteId(id));

    /// <summary>
    /// 🚨 The reason the OWNER is still derived from <see cref="SatelliteTableMapping.OwnerOfSatellitePath"/>
    /// over the NAMESPACE rather than by scanning the whole path: a PARTITION may legitimately be
    /// named with a leading underscore. <c>GlobalSettingsNodeType.SettingsPath</c> is literally
    /// <c>_Setting</c>, and a path scan would resolve <c>_Setting/_Policy</c>'s owner to <c>""</c> —
    /// the empty prefix that <c>COALESCE(main_node, namespace)</c> projects as a ROOT-scope grant
    /// over every partition. The namespace-based derivation leaves it intact.
    /// </summary>
    [Fact]
    public void OwnerOfAnUnderscoreNamedPartition_IsThePartition_NotRoot()
    {
        Assert.True(SatelliteTableMapping.IsSatelliteId("_Policy"),
            "the POLICY is the satellite — that is what triggers derivation");
        Assert.Equal("_Setting", SatelliteTableMapping.OwnerOfSatellitePath("_Setting"));
        Assert.NotEqual("", SatelliteTableMapping.OwnerOfSatellitePath("_Setting"));
    }

    /// <summary>
    /// <see cref="SatelliteTableMapping.IsSiblingSatellite"/> — the node-level classification shared
    /// by the create/upsert normalization and the static-seed guard. Its two carve-outs are the ones
    /// a purely syntactic id test gets wrong, and both are real nodes in this repo.
    /// </summary>
    [Fact]
    public void IsSiblingSatellite_ExcludesPartitionRootsAndPartitionDeclarations()
    {
        // The population it IS for.
        Assert.True(SatelliteTableMapping.IsSiblingSatellite(new MeshNode("_Policy", "Teams")));
        Assert.True(SatelliteTableMapping.IsSiblingSatellite(
            new MeshNode("_DefaultInstallLedger", "Plugins")));

        // 🚨 A PARTITION ROOT whose own name starts with an underscore. `_Setting` is a real
        // top-level partition (GlobalSettingsNodeType.SettingsPath); it is a main node.
        Assert.False(SatelliteTableMapping.IsSiblingSatellite(new MeshNode("_Setting")));

        // 🚨 A partition DECLARATION. `Admin/Partition/_Access` declares the global root-scope
        // access partition — its id is that partition's NAME, and it is a main node of the catalog
        // that lists it. Re-pointing its MainNode would delete it from that catalog's listing.
        Assert.False(SatelliteTableMapping.IsSiblingSatellite(
            new MeshNode("_Access", "Admin/Partition")));

        // Ordinary content is never a sibling satellite.
        Assert.False(SatelliteTableMapping.IsSiblingSatellite(new MeshNode("Readme", "Teams")));
    }

    /// <summary>
    /// The two classifications answer different questions and must stay separable: every enumerated
    /// <see cref="SatelliteTableMapping.Defaults"/> segment is satellite-SHAPED, but the converse is
    /// false — <c>_Policy</c> is a satellite that lives in <c>mesh_nodes</c>. Merging them would move
    /// <c>_Policy</c> to a satellite table and hide it from the permission evaluator.
    /// </summary>
    [Fact]
    public void EveryTableRoutedSatelliteSegment_IsAlsoSatelliteShaped()
    {
        var underscoreSegments = SatelliteTableMapping.Defaults
            .Select(m => m.Segment)
            .Where(s => s.StartsWith('_'))
            .ToArray();
        Assert.NotEmpty(underscoreSegments);
        foreach (var segment in underscoreSegments)
            Assert.True(SatelliteTableMapping.IsSatelliteId(segment),
                $"'{segment}' routes to a satellite table, so it must also read as satellite-shaped");

        // …and the converse does NOT hold, which is the whole point.
        Assert.True(SatelliteTableMapping.IsSatelliteId("_Policy"));
        Assert.False(SatelliteTableMapping.IsSatellitePath("Space/_Policy"));
    }
}

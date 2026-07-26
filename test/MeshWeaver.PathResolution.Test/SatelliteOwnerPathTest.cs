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
}

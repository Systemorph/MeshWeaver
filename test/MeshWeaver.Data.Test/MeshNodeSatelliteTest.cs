using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Data.Test;

public class MeshNodeSatelliteTest
{
    [Fact]
    public void A_satellite_points_at_its_main_node_not_at_itself()
    {
        var policy = MeshNode.Satellite("_Policy", "Teams");
        Assert.Equal("Teams/_Policy", policy.Path);
        Assert.Equal("Teams", policy.MainNode);
        Assert.NotEqual(policy.Path, policy.MainNode); // the catalog's is:main predicate
    }

    /// <summary>The trap this factory exists to close: the plain constructor makes a MAIN node.</summary>
    [Fact]
    public void The_plain_constructor_defaults_MainNode_to_itself()
    {
        var node = new MeshNode("_Policy", "Teams");
        Assert.Equal(node.Path, node.MainNode);
    }
}

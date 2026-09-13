// <meshweaver>
// Id: Testing/Data/MeshNodeRebaseTest
// DisplayName: Testing/Data/MeshNodeRebaseTest — migrated from xunit (convert-xunit-to-inmesh.py)
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
using MeshWeaver.Mesh;

/// <summary>
/// 🚨 MeshWeaver#2939. <see cref="MeshNode.Path"/> is COMPUTED, so it follows a record copy;
/// <see cref="MeshNode.MainNode"/> is STORED and its default is evaluated ONCE, at construction.
/// A <c>with { Namespace = … }</c> rebase therefore moves the path and leaves MainNode naming the
/// namespace the node was BORN in — and because the field is non-nullable, that stale value is
/// indistinguishable on the wire from a deliberate satellite pointer, so every writer persists it
/// and the node drops out of <c>is:main</c> (SQL <c>n.main_node = n.path</c>) with nothing logged.
/// <see cref="MeshNode.WithPath"/> is the rebase that cannot get this wrong.
/// </summary>
public class MeshNodeRebaseTest
{
    /// <summary>The bug, stated as an executable fact — the shape to grep for and eliminate.</summary>
    [MeshFact]
    public void A_plain_with_Namespace_rebase_leaves_MainNode_behind()
    {
        var minted = new MeshNode("deployment", "Skill");
        var rebased = minted with { Namespace = "Hosting/Skill" };

        Assert.Equal("Hosting/Skill/deployment", rebased.Path);
        Assert.Equal("Skill/deployment", rebased.MainNode);
        Assert.NotEqual(rebased.Path, rebased.MainNode);
        Assert.True(rebased.HasExplicitMainNode,
            "and it reads as DELIBERATE — which is why every upsert faithfully persisted it");
    }

    [MeshFact]
    public void WithPath_moves_MainNode_together_with_the_path()
    {
        var rebased = new MeshNode("deployment", "Skill").WithNamespace("Hosting/Skill");

        Assert.Equal("Hosting/Skill/deployment", rebased.Path);
        Assert.Equal(rebased.Path, rebased.MainNode);
        Assert.False(rebased.HasExplicitMainNode);
    }

    [MeshFact]
    public void WithPath_moves_the_id_too()
    {
        var rebased = new MeshNode("old", "A").WithPath("new", "B/C");

        Assert.Equal("B/C/new", rebased.Path);
        Assert.Equal("B/C/new", rebased.MainNode);
    }

    /// <summary>
    /// An AUTHORED MainNode is the writer's deliberate choice and survives the rebase: an
    /// <c>_Access</c> grant's MainNode IS its scope, and the permission evaluator silently ignores
    /// a grant whose MainNode is wrong.
    /// </summary>
    [MeshFact]
    public void WithPath_preserves_an_explicit_MainNode()
    {
        var satellite = MeshNode.Satellite("_Policy", "Teams").WithNamespace("Space/Teams");

        Assert.Equal("Space/Teams/_Policy", satellite.Path);
        Assert.Equal("Teams", satellite.MainNode);
    }

    [MeshFact]
    public void WithPath_to_the_root_leaves_a_bare_MainNode()
    {
        var rebased = new MeshNode("deployment", "Skill").WithNamespace(null);

        Assert.Equal("deployment", rebased.Path);
        Assert.Equal("deployment", rebased.MainNode);
    }
}

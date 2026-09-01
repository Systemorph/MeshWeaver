using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the RECOMPILE derivation — which NodeTypes an update must release after it landed a set of
/// node changes, and in what order.
///
/// <para><b>The incident class.</b> A GitSync update imports new <c>Source/*.cs</c> nodes and used
/// to walk away: every affected NodeType kept serving its STALE assembly until a human recompiled
/// (the documented "sync → recompile the changed NodeTypes → verify compiledSources" ritual; the
/// 2026-08-04 <c>ParameterSegment</c> incident is the cross-type variant — the fix was merged and
/// synced, and the page still failed because a SHARER of the edited file was never recompiled).
/// These are pure functions over paths and declared queries, so the rules are pinned without a hub
/// or a compile.</para>
/// </summary>
public class RecompileClosureTest
{
    /// <summary>The generalized ParameterSegment shape: one owner, one sharer, one bystander.</summary>
    private static Dictionary<string, NodeTypeDefinition?> SharedShape() => new()
    {
        ["SST/ParameterSegment"] = new NodeTypeDefinition(),
        ["SST/StandReModel"] = new NodeTypeDefinition
        {
            Sources = ["namespace:Source scope:subtree", "shared=@SST/ParameterSegment/Source"],
        },
        ["SST/Unrelated"] = new NodeTypeDefinition(),
    };

    [Fact]
    public void OwnSourceChange_AffectsOwnerAndEverySharer()
    {
        var affected = RecompileClosure.AffectedTypes(
            SharedShape(), ["SST/ParameterSegment/Source/Segment"]);

        affected.Should().Equal(["SST/ParameterSegment", "SST/StandReModel"],
            "the owner compiles its own Source subtree and the sharer compiles the same file "
            + "into ITS assembly — recompiling only the owner is exactly the ParameterSegment "
            + "incident");
    }

    [Fact]
    public void ContentOnlyChange_AffectsNothing()
    {
        var affected = RecompileClosure.AffectedTypes(
            SharedShape(), ["SST/Docs/Welcome", "SST/Filing/2026"]);

        affected.Should().BeEmpty(
            "a content-only update must trigger ZERO recompiles — releasing types on every sync "
            + "is the compile-storm failure mode");
    }

    [Fact]
    public void TypeNodeItselfChanged_AffectsThatType()
    {
        var affected = RecompileClosure.AffectedTypes(
            SharedShape(), ["SST/ParameterSegment"]);

        affected.Should().Equal(["SST/ParameterSegment"],
            "an edited authored definition (Configuration / Sources) must recompile — but it is "
            + "not a change to any shared FILE, so sharers keep their correct assemblies");
    }

    [Fact]
    public void TestSubtreeChange_AffectsTheOwner()
    {
        var affected = RecompileClosure.AffectedTypes(
            SharedShape(), ["SST/ParameterSegment/Test/SegmentTests"]);

        affected.Should().Contain("SST/ParameterSegment",
            "tests are compiled into the same assembly as sources");
    }

    /// <summary>
    /// NOT transitive, by design: a sharer compiles the shared file's TEXT into its own assembly,
    /// so a type sharing the SHARER's own sources holds text that did not change.
    /// </summary>
    [Fact]
    public void AffectedSet_IsNotTransitiveThroughSharers()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition(),
            ["B"] = new NodeTypeDefinition { Sources = ["namespace:Source scope:subtree", "shared=@A/Source"] },
            ["C"] = new NodeTypeDefinition { Sources = ["namespace:Source scope:subtree", "shared=@B/Source"] },
        };

        var affected = RecompileClosure.AffectedTypes(types, ["A/Source/X"]);

        affected.Should().Equal(["A", "B"],
            "C compiles B's OWN sources, which did not change — its assembly is already correct, "
            + "and a transitive closure would recompile half the mesh on every edit");
    }

    [Fact]
    public void EmptyChangeSet_IsEmpty()
    {
        RecompileClosure.AffectedTypes(SharedShape(), []).Should().BeEmpty();
        RecompileClosure.AffectedTypes(
                new Dictionary<string, NodeTypeDefinition?>(), ["A/Source/X"])
            .Should().BeEmpty();
    }

    [Fact]
    public void OrderAffected_DependenciesFirst()
    {
        var types = SharedShape();
        var affected = RecompileClosure.AffectedTypes(types, ["SST/ParameterSegment/Source/Segment"]);

        var ordered = RecompileClosure.OrderAffected(types, affected, out var cyclic);

        cyclic.Should().BeEmpty();
        ordered.Should().Equal(["SST/ParameterSegment", "SST/StandReModel"],
            "the sharer's compile pulls the owner's sources, so the owner settles first — the "
            + "same order the pre-warmer compiles in");
    }

    /// <summary>
    /// The order must respect edges THROUGH types that are not themselves affected — filtering the
    /// full topological order (rather than ordering an affected-only subgraph) is what guarantees
    /// this.
    /// </summary>
    [Fact]
    public void OrderAffected_RespectsEdgesThroughUnaffectedIntermediates()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["A"] = new NodeTypeDefinition(),
            ["B"] = new NodeTypeDefinition { Sources = ["shared=@A/Source"] },
            ["C"] = new NodeTypeDefinition { Sources = ["shared=@B/Source"] },
        };

        var ordered = RecompileClosure.OrderAffected(types, ["C", "A"], out var cyclic);

        cyclic.Should().BeEmpty();
        ordered.Should().Equal(["A", "C"],
            "A precedes C via the unaffected intermediate B — an affected-only subgraph would "
            + "have no edge between them and could emit them in either order");
    }

    [Fact]
    public void OrderAffected_CycleIsReportedAndStillReleased()
    {
        var types = new Dictionary<string, NodeTypeDefinition?>
        {
            ["X"] = new NodeTypeDefinition { Sources = ["namespace:Source scope:subtree", "shared=@Y/Source"] },
            ["Y"] = new NodeTypeDefinition { Sources = ["namespace:Source scope:subtree", "shared=@X/Source"] },
        };

        var ordered = RecompileClosure.OrderAffected(types, ["X", "Y"], out var cyclic);

        cyclic.Should().Equal(["X", "Y"], "the cycle must be SEEN — callers log it loudly");
        ordered.OrderBy(p => p).Should().Equal(new[] { "X", "Y" },
            "a cycle must never silently DROP a recompile — both types are still released, in "
            + "deterministic flush order");
    }

    [Fact]
    public void AffectedTypes_NullDefinitionUsesDefaultOwnSubtree()
    {
        var types = new Dictionary<string, NodeTypeDefinition?> { ["A"] = null };

        RecompileClosure.AffectedTypes(types, ["A/Source/X"]).Should().Equal(["A"]);
        RecompileClosure.AffectedTypes(types, ["A/Elsewhere/X"]).Should().BeEmpty();
    }
}

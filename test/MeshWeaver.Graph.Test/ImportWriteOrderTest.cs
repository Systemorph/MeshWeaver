using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the static-repo import's WRITE ORDER — a node type before the instances that name it
/// (issue #2556) — and the CYCLE POLICY that keeps it total.
///
/// <para><b>The incident.</b> The importer wrote a source's nodes in whatever order the source
/// enumerated them. The create pipeline refuses a node whose <c>NodeType</c> names nothing the mesh
/// knows, so a repo shipping an instance of a type it introduces had that instance refused whenever
/// enumeration put it first — and the #2229 baseline guard held the baseline so a later pass would
/// retry the IDENTICAL ordering and fail identically. memex-cloud measured 6,902 refusals in 90
/// minutes and one node refused 40 times in 120.</para>
///
/// <para>Pure functions over paths, so the ordering rules are pinned without a hub, a mesh or a
/// compile — the same way <see cref="NodeTypeDependencyGraphTest"/> pins the compile order.</para>
/// </summary>
public class ImportWriteOrderTest
{
    private const string P = "FutuRe";

    /// <summary>The production shape from the issue: the instance sorts before its type.</summary>
    private static readonly MeshNode[] TransactionMappingShape =
    [
        Instance($"{P}/EuropeRe/TransactionMapping/EUR-COMM_FIRE-PROP", $"{P}/TransactionMapping"),
        Instance($"{P}/EuropeRe/TransactionMapping/EUR-COMM_LIAB", $"{P}/TransactionMapping"),
        Code($"{P}/TransactionMapping/Source/TransactionMapping.cs"),
        Type($"{P}/TransactionMapping"),
    ];

    [Fact]
    public void TypeIsWrittenBeforeEveryInstanceThatNamesIt()
    {
        var plan = ImportWriteOrder.Plan(TransactionMappingShape);

        // The refusal in the issue, verbatim: an instance of FutuRe/TransactionMapping written before
        // the node at that path exists. Whatever the stage numbers are, this inequality is the fix.
        plan.StageOfPath[$"{P}/TransactionMapping"]
            .Should().BeLessThan(plan.StageOfPath[$"{P}/EuropeRe/TransactionMapping/EUR-COMM_FIRE-PROP"],
                "an instance written before its type node exists is refused 'NodeType … is not registered', "
                + "and the retry re-runs the same ordering forever (#2556)");
        plan.Cyclic.Should().BeEmpty();
    }

    [Fact]
    public void ATypesCompileInputsAreWrittenBeforeTheTypeItself()
    {
        var plan = ImportWriteOrder.Plan(TransactionMappingShape);

        // Creating the NodeType is what triggers the compile that reads its Source children — the same
        // ordering PackageInstaller.InstallNodeRepo has applied to node-repo installs since #815.
        plan.StageOfPath[$"{P}/TransactionMapping/Source/TransactionMapping.cs"]
            .Should().BeLessThan(plan.StageOfPath[$"{P}/TransactionMapping"]);
    }

    /// <summary>
    /// 🚨 The trap <c>PackageInstaller</c>'s bucket 0 documents: "compile inputs first" must mean
    /// <c>Source/</c> and <c>Test/</c> only. Widening it to "any descendant of a type path" drags a
    /// typed instance NESTED under its leaf-shaped type ahead of the very type it needs — turning the
    /// fix into the bug it fixes, on the shape (<c>ClaimsDeepfield/Cedent/NSV</c>) that actually ships.
    /// </summary>
    [Fact]
    public void ANestedInstanceIsADependentOfItsType_NotACompileInput()
    {
        var plan = ImportWriteOrder.Plan(
        [
            Instance("Cl/Cedent/NSV", "Cl/Cedent"),
            Type("Cl/Cedent"),
            Code("Cl/Cedent/Source/Cedent.cs"),
        ]);

        plan.StageOfPath["Cl/Cedent"].Should().BeLessThan(plan.StageOfPath["Cl/Cedent/NSV"],
            "a typed instance nested under its own type is a DEPENDENT, never a compile input");
        plan.StageOfPath["Cl/Cedent/Source/Cedent.cs"].Should().BeLessThan(plan.StageOfPath["Cl/Cedent"]);
        plan.Cyclic.Should().BeEmpty(
            "widening 'compile inputs' to any descendant makes a type and its own nested instance "
            + "mutually dependent — a cycle invented by the ordering rule itself");
    }

    /// <summary>
    /// The plan must be TOTAL: every input node written exactly once, whatever the graph looks like.
    /// A node the ordering drops is a node nobody imports — strictly worse than one written late.
    /// </summary>
    [Fact]
    public void EveryInputNodeIsWrittenExactlyOnce_IncludingDuplicatePaths()
    {
        MeshNode[] nodes =
        [
            Instance("S/A", "S/T"), Type("S/T"), Markdown("S/Plain"),
            Markdown("S/Plain"),   // a source may yield the same path twice
            Instance("S/B", "S/T"),
        ];

        var plan = ImportWriteOrder.Plan(nodes);

        plan.Stages.SelectMany(s => s).Should().HaveCount(nodes.Length);
        plan.Stages.SelectMany(s => s).Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal)
            .Should().Equal(nodes.Select(n => n.Path).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// An unconstrained source keeps the enumeration order it always had — the ordering may only move
    /// what the dependency graph actually constrains, or every partition's import changes shape.
    /// </summary>
    [Fact]
    public void NodesWithNoDependencies_KeepTheSourcesOwnOrderInOneStage()
    {
        MeshNode[] nodes = [Markdown("S/C"), Markdown("S/A"), Markdown("S/B")];

        var plan = ImportWriteOrder.Plan(nodes);

        plan.Stages.Should().HaveCount(1);
        plan.Stages[0].Select(n => n.Path).Should().Equal("S/C", "S/A", "S/B");
    }

    // ——— Cycle policy ———————————————————————————————————————————————————————————————

    /// <summary>
    /// 🚨 THE CYCLE POLICY. Two nodes that type each other cannot both be written second, so no order
    /// is correct — but the plan must still be TOTAL, DETERMINISTIC and HONEST: every member emitted
    /// exactly once, in path order, and NAMED in <see cref="ImportWritePlan.Cyclic"/> so the import can
    /// report it instead of failing mysteriously. Failing the whole import instead is not an option:
    /// the marker/short-circuit logic reads a partial import's verdict, and one malformed pair would
    /// take every other node in the partition down with it.
    /// </summary>
    [Fact]
    public void ACycle_IsEmittedOnce_InPathOrder_AndReported()
    {
        var plan = ImportWriteOrder.Plan([Type("S/B", typedBy: "S/A"), Type("S/A", typedBy: "S/B")]);

        plan.Stages.SelectMany(s => s).Select(n => n.Path)
            .OrderBy(p => p, StringComparer.Ordinal).Should().Equal("S/A", "S/B");
        plan.Cyclic.OrderBy(p => p, StringComparer.Ordinal).Should().Equal("S/A", "S/B");
        plan.StageOfPath["S/A"].Should().BeLessThan(plan.StageOfPath["S/B"],
            "no order is correct, so the order must at least be DETERMINISTIC — smallest path first, "
            + "so which node gets refused is reproducible instead of a race");
    }

    /// <summary>
    /// 🚨 A CYCLE IS NOT DEMOTED TO LAST (#1347). That was a real regression in the compile order: the
    /// Store cycle, and the whole paywall chain behind it, were made cheap last for the crime of being
    /// a cycle. The import inherits the same peel, so the same guarantee must hold — an independent
    /// node ordering AFTER a cycle it does not depend on would be that bug, re-introduced.
    /// </summary>
    [Fact]
    public void ACycle_DoesNotPushIndependentNodesLater()
    {
        var plan = ImportWriteOrder.Plan(
            [Type("S/B", typedBy: "S/A"), Type("S/A", typedBy: "S/B"), Markdown("S/Independent")]);

        plan.StageOfPath["S/Independent"].Should().Be(0,
            "an independent node waits on nothing; a cycle elsewhere in the source is not its problem");
    }

    /// <summary>
    /// Nodes DOWNSTREAM of a cycle are not part of it and still order behind it — the distinction the
    /// condensation exists to preserve.
    /// </summary>
    [Fact]
    public void NodesDownstreamOfACycle_StillOrderBehindIt()
    {
        var plan = ImportWriteOrder.Plan(
            [Instance("S/Inst", "S/A"), Type("S/B", typedBy: "S/A"), Type("S/A", typedBy: "S/B")]);

        plan.StageOfPath["S/Inst"].Should().BeGreaterThan(plan.StageOfPath["S/A"]);
        plan.Cyclic.Should().NotContain("S/Inst");
    }

    /// <summary>
    /// A node typed by ITSELF is not a cycle to report — a self-edge is no ordering constraint, and
    /// treating it as one would name a one-member "cycle" on a perfectly ordinary self-typed root.
    /// </summary>
    [Fact]
    public void ASelfTypedNode_IsNotReportedAsACycle()
    {
        var plan = ImportWriteOrder.Plan([Type("S/Self", typedBy: "S/Self")]);

        plan.Cyclic.Should().BeEmpty();
        plan.Stages.SelectMany(s => s).Should().HaveCount(1);
    }

    // ——— TypeIsOrderedAhead: what the importer asks per node ——————————————————————————

    [Fact]
    public void TypeIsOrderedAhead_IsTrueOnlyWhenThisPassReallyWritesTheTypeFirst()
    {
        var inImport = Instance("S/A", "S/T");
        var foreign = Instance("S/B", "Other/Partition/T");
        var plan = ImportWriteOrder.Plan([inImport, foreign, Type("S/T")]);

        ImportWriteOrder.TypeIsOrderedAhead(plan, inImport).Should().BeTrue();
        ImportWriteOrder.TypeIsOrderedAhead(plan, foreign).Should().BeFalse(
            "a type carried by no source in this pass cannot be ordered into existence — that node's "
            + "type has to be probed against the mesh instead (#2556, the cross-partition half)");
    }

    [Fact]
    public void TypeIsOrderedAhead_IsFalseForACycleMember()
    {
        var a = Type("S/A", typedBy: "S/B");
        var plan = ImportWriteOrder.Plan([a, Type("S/B", typedBy: "S/A")]);

        ImportWriteOrder.TypeIsOrderedAhead(plan, a).Should().BeFalse(
            "its type IS carried by this import — just not ahead of it, which is exactly the case the "
            + "blocked-create classification exists for");
    }

    /// <summary>
    /// Mesh paths do not distinguish case (the #1326 <c>release/</c> vs <c>Release/</c> lesson), so a
    /// case-only difference between an instance's NodeType and the type node's path must NOT read as
    /// "a foreign type" — that would report a perfectly importable node as blocked.
    /// </summary>
    [Fact]
    public void TypeReferencesAreMatchedCaseInsensitively()
    {
        var instance = Instance("S/A", "s/t");
        var plan = ImportWriteOrder.Plan([instance, Type("S/T")]);

        plan.StageOfPath["S/T"].Should().BeLessThan(plan.StageOfPath["S/A"]);
        ImportWriteOrder.TypeIsOrderedAhead(plan, instance).Should().BeTrue();
    }

    /// <summary>
    /// The classifier must survive the round trip: a NodeType node read back on a hub that does not
    /// know <see cref="NodeTypeDefinition"/> carries its content as an untyped <c>JsonElement</c>, so
    /// a <c>Content is NodeTypeDefinition</c> test alone silently answers "not a type" — the exact
    /// trap-door <c>ObjectAsExtensions</c> exists for. The <c>NodeType</c> field is the durable half.
    /// </summary>
    [Fact]
    public void ATypeNodeIsRecognisedFromItsNodeTypeField_NotOnlyFromTypedContent()
    {
        var degraded = new MeshNode("T", "S")
        {
            NodeType = MeshNode.NodeTypePath,
            Content = JsonSerializer.SerializeToElement(new { configuration = "config => config" }),
        };

        ImportWriteOrder.IsNodeTypeDefinition(degraded).Should().BeTrue();

        var plan = ImportWriteOrder.Plan([Code("S/T/Source/T.cs"), degraded]);
        plan.StageOfPath["S/T/Source/T.cs"].Should().BeLessThan(plan.StageOfPath["S/T"]);
    }

    [Fact]
    public void EmptyInput_YieldsAnEmptyPlan()
    {
        var plan = ImportWriteOrder.Plan(Array.Empty<MeshNode>());

        plan.Stages.Should().BeEmpty();
        plan.Cyclic.Should().BeEmpty();
        plan.StageOfPath.Count.Should().Be(0);
    }

    // ——— fixtures ————————————————————————————————————————————————————————————————————

    private static MeshNode At(string path, string nodeType)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0
            ? new MeshNode(path) { NodeType = nodeType, State = MeshNodeState.Active }
            : new MeshNode(path[(slash + 1)..], path[..slash])
            { NodeType = nodeType, State = MeshNodeState.Active };
    }

    private static MeshNode Instance(string path, string typePath) => At(path, typePath);

    private static MeshNode Markdown(string path) => At(path, "Markdown");

    private static MeshNode Code(string path) => At(path, "Code");

    private static MeshNode Type(string path, string? typedBy = null) =>
        At(path, typedBy ?? MeshNode.NodeTypePath) with
        {
            Content = new NodeTypeDefinition { Configuration = "config => config" }
        };
}

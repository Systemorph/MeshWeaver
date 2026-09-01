using System;
using System.Diagnostics;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 THE PLAN MUST NOT BE QUADRATIC IN THE NUMBER OF IMPORTED NODES.
///
/// <para><b>The trap this pins.</b> <see cref="ImportWriteOrder"/> reuses
/// <c>NodeTypeDependencyGraph.TopologicalOrder</c> — deliberately, so import order and compile order
/// cannot disagree. But its condensation computes a reachability closure per vertex and then groups
/// components by scanning every key for every unassigned one: <b>O(V²)</b>. That is right for the few
/// hundred dynamic NodeTypes it was written for, and wrong for a graph with one vertex per IMPORTED
/// NODE. Measured on the shape a real import has — thousands of instances of one type — before the
/// core/leaf split:</para>
///
/// <code>
/// n=500    17 ms      n=5000    688 ms
/// n=2000  112 ms      n=10000  2668 ms      (16× per 4× — textbook quadratic)
/// </code>
///
/// <para>Only the CORE (paths something else depends on) is condensed; the instances are leaves,
/// which cannot be in a cycle and whose stage follows from their already-staged dependencies. That
/// makes the peel run over the type nodes and their compile inputs — a handful — whatever the node
/// count.</para>
///
/// <para>🚨 The bound below pins a COMPLEXITY CLASS, not a performance target: it is two orders of
/// magnitude above the post-fix time and below the pre-fix time, so only an algorithmic regression
/// can trip it. If it fires, the answer is never a larger bound — it is that the peel is seeing
/// vertices it should not.</para>
/// </summary>
public class ImportWriteOrderScaleTest(ITestOutputHelper output)
{
    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(10000)]
    public void ManyInstancesOfOneType_PlanIsCorrectAndNotQuadratic(int instances)
    {
        // The FutuRe shape at scale: one type, its compile input, and N instances of it.
        var nodes = Enumerable.Range(0, instances)
            .Select(i => new MeshNode($"Inst{i}", "S") { NodeType = "S/T", State = MeshNodeState.Active })
            .Append(new MeshNode("T", "S")
            {
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { Configuration = "config => config" },
            })
            .Append(new MeshNode("T.cs", "S/T/Source") { NodeType = "Code", State = MeshNodeState.Active })
            .ToArray();

        var stopwatch = Stopwatch.StartNew();
        var plan = ImportWriteOrder.Plan(nodes);
        stopwatch.Stop();
        output.WriteLine($"n={instances} elapsed={stopwatch.ElapsedMilliseconds}ms "
            + $"stages={plan.Stages.Count} cyclic={plan.Cyclic.Count}");

        // Correctness first — a fast plan that loses nodes or mis-orders them is worthless.
        plan.Stages.SelectMany(s => s).Should().HaveCount(nodes.Length);
        plan.Cyclic.Should().BeEmpty();
        plan.StageOfPath["S/T/Source/T.cs"].Should().BeLessThan(plan.StageOfPath["S/T"]);
        plan.StageOfPath["S/T"].Should().BeLessThan(plan.StageOfPath["S/Inst0"]);
        plan.StageOfPath["S/T"].Should().BeLessThan(plan.StageOfPath[$"S/Inst{instances - 1}"]);

        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000,
            "the peel must see the CORE (the type and its compile input), not one vertex per "
            + "instance — a quadratic condensation took 2,668 ms at this size");
    }
}

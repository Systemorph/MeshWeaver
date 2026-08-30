using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The built-in import templates (<c>Templates/Import/NodeCopy</c>, <c>Templates/Import/Mirror</c>)
/// are embedded <c>.csx</c> resources that Roslyn compiles at RUNTIME, inside the mesh —
/// <c>dotnet build</c> never sees them, so a break in one ships green and fails the first time an
/// operator runs a copy or a mirror.
///
/// <para>🚨 Found on 2026-08-30 while removing the forbidden observable-to-<c>Task</c> bridge from
/// both templates: <b>nothing in this repository ran them</b>. This test closes that, and it runs
/// them THROUGH THE MESH — an <c>ExecuteScriptRequest</c> aimed at the template node, exactly the
/// path <c>NodeCopyDispatchHandler</c> takes — rather than compiling the source with a
/// hand-assembled Roslyn setup. A private compile would prove only that MY reference set works;
/// the mesh's own kernel is what has to accept the script, and its reference set deliberately
/// filters test scaffolding so a test cannot pass against APIs production lacks (the export
/// templates once bound a test-only <c>QueryAsync</c>, passed eleven tests that really executed
/// them, and threw <c>CS1061</c> on the first real export).</para>
/// </summary>
public class BuiltInScriptTemplatesCompileTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// A copy the template really performs: the source subtree exists, the run is dispatched at the
    /// template node, and the copy lands. A runtime compile error, a missing using or a binding
    /// break surfaces here as a failed dispatch instead of reaching an operator.
    /// </summary>
    [Fact]
    public async Task NodeCopyTemplate_RunsInTheMesh_AndCopiesTheSubtree()
    {
        var source = $"{TestPartition}/copy-source";
        await NodeFactory.CreateNode(
            new MeshNode("copy-source", TestPartition)
            {
                Name = "Copy Source",
                NodeType = "Markdown"
            }).Should().Emit();

        var response = (await Mesh
            .Observe<NodeCopyDispatchResponse>(
                new NodeCopyDispatchRequest(source, $"{TestPartition}/copies") { Force = true },
                o => o.WithTarget(Mesh.Address))
            .Should().Within(120.Seconds()).Emit()).Message;

        response.Error.Should().BeNullOrEmpty(
            "the template is compiled at RUNTIME inside the mesh — an error here is a break "
            + $"`dotnet build` cannot see: {response.Error}");
        response.ActivityPath.Should().NotBeNullOrEmpty(
            "a dispatched run reports the Activity it landed at; without one there is nothing to "
            + "read a verdict from");

        // 🚨 THE START-ACK PROVES NOTHING. ScriptDispatch.StartScript answers as soon as the
        // Activity node exists — before Roslyn has compiled a single line — so asserting on the
        // response alone is a verification that cannot fail: a template with a syntax error passes
        // it. The verdict lives on the Activity, and the copied node is the independent witness.
        var workspace = Mesh.GetWorkspace();
        var activity = await workspace
            .GetMeshNodeStream(response.ActivityPath)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(a => a is { Status: ActivityStatus.Succeeded or ActivityStatus.Failed
                                       or ActivityStatus.Cancelled })
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            // ObserveCompletion, never .ToTask(): the safe bridge queues the continuation
            // instead of resuming this test inline on the signalling thread.
            .ObserveCompletion(
                ex => Output.WriteLine($"[TEST] late fault after the wait settled: {ex}"),
                TestContext.Current.CancellationToken);

        activity!.Status.Should().Be(ActivityStatus.Succeeded,
            "a RUNTIME compile error, a missing using or a binding break in NodeCopy.csx surfaces "
            + "as a failed Activity and nowhere else: "
            + string.Join(" | ", activity.Messages.TakeLast(5).Select(m => m.Message)));

        var copied = await ReadNode($"{TestPartition}/copies/copy-source")
            .Should().Match(n => n is not null);
        copied!.Name.Should().Be("Copy Source",
            "the template must have actually copied the subtree — the Activity's own verdict and "
            + "the copied node are two independent witnesses, and a green test needs both");

        Output.WriteLine($"[TEST] NodeCopy ran in-mesh, activity: {response.ActivityPath}");
    }
}

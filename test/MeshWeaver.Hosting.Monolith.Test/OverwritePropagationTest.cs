using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Verifies the <c>Overwrite</c> primitive (the static-repo import's write path): a cross-hub
/// <c>GetMeshNodeStream(path).Overwrite(node)</c> lands the FULL authoritative node on the owning
/// hub as a <see cref="ChangeType.Full"/> and propagates to every mirror. Unlike a merge
/// <c>Update</c>, it replaces the whole node — a field present before the overwrite is gone after.
/// Mirrors <see cref="ThreeNodePropagationTest"/> but exercises Overwrite.
/// </summary>
public class OverwritePropagationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 30_000)]
    public void Overwrite_ReplacesFullNode_PropagatesViaOwner()
    {
        // 1. Create the owning node with TWO fields set (Name + Category).
        var pathA = $"{TestPartition}/ov";
        NodeFactory.CreateNode(
            new MeshNode("ov", TestPartition) { Name = "A0", Category = "C0", NodeType = "Markdown" })
            .Should().Within(20.Seconds()).Emit();

        // 2. Two client hubs, each with a remote stream to the owning node.
        var hubB = Mesh.ServiceProvider.CreateMessageHub(
            new Address("client", "ovb"), c => ConfigureClient(c).AddData())!;
        var hubC = Mesh.ServiceProvider.CreateMessageHub(
            new Address("client", "ovc"), c => ConfigureClient(c).AddData())!;

        var streamFromB = hubB.GetWorkspace().GetMeshNodeStream(pathA);
        var streamFromC = hubC.GetWorkspace().GetMeshNodeStream(pathA);

        var bEmissions = streamFromB.Select(ci => (Name: ci?.Name, Category: ci?.Category));
        var seen = new List<(string?, string?)>();
        using var subB = bEmissions.Subscribe(t =>
        {
            lock (seen) seen.Add(t);
            Output.WriteLine($"[b] emission: Name={t.Name ?? "(null)"} Category={t.Category ?? "(null)"}");
        });

        // 3. Both mirrors observe the initial full snapshot.
        bEmissions.Should().Within(10.Seconds()).Match(t => t.Name == "A0" && t.Category == "C0");
        streamFromC.Select(ci => ci?.Name).Should().Within(10.Seconds()).Match(n => n == "A0");

        // 4. C OVERWRITES with a full node that changes Name and DROPS Category. Identity
        //    (Id/Namespace) targets the same node; the owner stamps Version on the Full.
        Output.WriteLine("[c] issuing .Overwrite (Name='A1', Category dropped)");
        streamFromC.Overwrite(new MeshNode("ov", TestPartition) { Name = "A1", NodeType = "Markdown" })
            .Subscribe(_ => { }, ex => Output.WriteLine($"[c] overwrite error: {ex.Message}"));

        // 5. B observes the FULL replace: Name flipped AND the previously-set Category is gone.
        //    (A merge patch could keep Category; a Full overwrite replaces the whole node.)
        bEmissions.Should().Within(10.Seconds()).Match(
            t => t.Name == "A1" && t.Category == null,
            "Overwrite lands a full node — the Category set before the overwrite must be cleared");
        Output.WriteLine("[b] saw full overwrite propagation");
    }
}

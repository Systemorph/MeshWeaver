using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pure in-process isolation test for the workspace propagation bug class.
///
/// Setup: a per-node hub at <c>TestPartition/iso-node</c> (no AI / no thread
/// machinery). Subscribe to <c>workspace.GetStream(new MeshNodeReference())</c>
/// from inside the per-node hub's own workspace, then call
/// <c>workspace.UpdateMeshNode</c> from the same workspace and assert the
/// subscriber sees the new value.
///
/// <para>
/// Reproduces the bug behind the Thread/Delegation Orleans cluster:
/// <c>UpdateMeshNode</c> writes through the data source's primary
/// EntityStore stream (<c>dsStream.Update</c>), but the resolver in
/// <c>MeshDataSource.AddWorkspaceReferenceStream&lt;MeshNode&gt;</c> previously
/// routed <c>workspace.GetStream(new MeshNodeReference())</c> through
/// <c>workspace.GetStream(new CollectionReference("MeshNode"))</c> — a
/// separately cached reduced stream that doesn't share state with the
/// primary. Subscribers got the initial snapshot but no subsequent update
/// emission. Fix: reduce directly from the data source's primary stream.
/// </para>
/// </summary>
public class WorkspaceUpdateMeshNodePropagationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    [Fact(Timeout = 30_000)]
    public async Task UpdateMeshNode_PropagatesToOwnSubscribers()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        // 1. Create a Markdown node — same path pattern as ThreeNodePropagationTest
        //    so the shared TestPartition routing already works.
        var path = $"{TestPartition}/iso-node";
        await NodeFactory.CreateNode(
            new MeshNode("iso-node", TestPartition) { Name = "initial", NodeType = "Markdown" });
        Output.WriteLine($"Created node: {path}");

        // 2. Activate the per-node hub by sending it any inbound message — Orleans
        //    grain activation is lazy. Monolith hubs activate eagerly via routing
        //    so this is a no-op there but keeps the test portable.
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .FirstAsync().ToTask(ct);

        // 3. Reach into the per-node hub's own workspace.
        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("hub should be activated by the GetDataRequest above");
        var workspace = nodeHub!.GetWorkspace();

        // 4. Subscribe to MeshNodeReference (own) — the same primitive
        //    ThreadSubmissionServer.InstallServerWatcher uses.
        var emissions = new List<MeshNode>();
        using var sub = workspace.GetStream(new MeshNodeReference())!
            .Where(change => change.Value != null)
            .Subscribe(change => emissions.Add(change.Value!));

        // Initial snapshot.
        var deadline1 = DateTime.UtcNow.AddSeconds(5);
        while (emissions.Count == 0 && DateTime.UtcNow < deadline1)
            await Task.Delay(50, ct);
        emissions.Should().NotBeEmpty("subscriber must receive initial MeshNode snapshot");
        emissions[^1].Name.Should().Be("initial");
        Output.WriteLine($"Initial snapshot OK: {emissions.Count} emission(s)");

        // 5. Write through workspace.GetMeshNodeStream().Update — this goes via the
        //    data source's primary EntityStore stream. Subscribe is mandatory: the API
        //    returns a cold IObservable<MeshNode>; the side effect runs on Subscribe.
        var beforeCount = emissions.Count;
        var marker = $"updated-{Guid.NewGuid().ToString("N")[..8]}";
        using var updateSub = workspace.GetMeshNodeStream()
            .Update(node => node with { Name = marker })
            .Subscribe(
                _ => Output.WriteLine($"UpdateMeshNode emission: {marker}"),
                ex => Output.WriteLine($"UpdateMeshNode error: {ex.Message}"));

        // 6. The subscriber MUST receive the new state. This is the propagation
        //    contract — without the resolver fix, the subscriber received the
        //    initial snapshot but no update emission and this assertion timed out.
        var deadline2 = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline2)
        {
            if (emissions.Any(n => n.Name == marker)) break;
            await Task.Delay(50, ct);
        }

        Output.WriteLine($"After UpdateMeshNode: emissions={emissions.Count}, " +
                         $"marker={marker}, latest.Name='{emissions[^1].Name}'");

        emissions.Count.Should().BeGreaterThan(beforeCount,
            "UpdateMeshNode must produce a new emission on the MeshNodeReference subscriber");
        emissions.Should().Contain(n => n.Name == marker,
            "the new emission must carry the value written via UpdateMeshNode — " +
            "the resolver in MeshDataSource.AddWorkspaceReferenceStream<MeshNode> for " +
            "MeshNodeReference() (Path=null) must reduce from the data source's PRIMARY " +
            "EntityStore stream, not from workspace.GetStream(CollectionReference) (a " +
            "separately cached reduced stream that doesn't see dsStream.Update writes).");
    }
}

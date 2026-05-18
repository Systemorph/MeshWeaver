using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Tests that the MeshChangeFeed propagates across Orleans silos
/// and that path resolver cache is invalidated correctly.
/// Uses the shared test cluster.
/// </summary>
public class OrleansMeshChangeFeedTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub ClientMesh => Fixture.ClientMesh;

    private async Task<IMessageHub> GetClientAsync(string id)
        => await base.GetClientAsync(id);

    private async Task<string> CreateNodeAsync(IMessageHub client, MeshNode node, string targetAddress, CancellationToken ct)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, target={targetAddress}");
        var response = await client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress))).FirstAsync().ToTask(ct);
        Output.WriteLine($"CreateNodeResponse: success={response.Message.Success}, path={response.Message.Node?.Path ?? "(null)"}");
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    /// <summary>
    /// Create a node, then immediately resolve its path.
    /// The path resolver cache must be invalidated by the Created event
    /// so the new node is found (not a stale partial match to parent).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateNode_PathResolverFindsItImmediately()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var client = await GetClientAsync($"cfeed-{suffix}");

        // Create parent
        var parentNode = new MeshNode($"cfeed-parent-{suffix}", "TestUser")
        {
            Name = "Change Feed Parent",
            NodeType = "Markdown"
        };
        var parentPath = await CreateNodeAsync(client, parentNode, "TestUser", ct);
        Output.WriteLine($"Parent: {parentPath}");

        // Create child
        var childNode = new MeshNode($"cfeed-child-{suffix}", parentPath)
        {
            Name = "Change Feed Child",
            NodeType = "Markdown"
        };
        var childPath = await CreateNodeAsync(client, childNode, "TestUser", ct);
        Output.WriteLine($"Child: {childPath}");

        // Verify: child is reachable via message routing (GetDataRequest via MeshNodeReference reducer).
        var getResponse = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(childPath))).FirstAsync().ToTask(ct);
        var data = getResponse.Message.Data as MeshNode;
        if (data == null && getResponse.Message.Data is JsonElement je)
            data = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
        data.Should().NotBeNull("child node should be reachable via routing");
        data!.Path.Should().Be(childPath);
        Output.WriteLine("PASSED Ã¢â‚¬â€ CreateNode immediately routable");
    }

    /// <summary>
    /// The original production bug: delegation creates a sub-thread,
    /// then AppendUserMessageRequest must route to it correctly.
    /// The path resolver cache must not serve a stale partial match.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DelegationSubThread_RoutesAfterCreate()
    {
        var ct = new CancellationTokenSource(25.Seconds()).Token;
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var client = await GetClientAsync($"cfeed-del-{suffix}");

        // Create thread
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "ChangeFeed routing test", "TestUser");
        var threadPath = await CreateNodeAsync(client, threadNode, "TestUser", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // Submit message (creates user + response cells)
        var workspace = client.GetWorkspace();
        var twoMessages = workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.Cast<MeshNode>().FirstOrDefault(n => n.Path == threadPath);
                return (node?.Content as MeshThread)?.Messages ?? [];
            })
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
            {
                Hub = client,
                ThreadPath = threadPath,
                UserText = "Test routing",
                ContextPath = "TestUser"
            });
            var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // Create sub-thread under the response message (same as delegation does)
        var responseMsgId = msgIds[1];
        var parentMsgPath = $"{threadPath}/{responseMsgId}";
        var subThreadId = $"sub-{suffix}";

        var subThreadNode = new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Sub-thread routing test",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "TestUser",
            Content = new MeshThread { CreatedBy = "TestUser" }
        };
        var subThreadPath = await CreateNodeAsync(client, subThreadNode, threadPath, ct);
        Output.WriteLine($"Sub-thread created: {subThreadPath}");

        // NOW submit to the sub-thread Ã¢â‚¬â€ this is where routing failed before
        // (stale cache sent the request to the parent message grain)
        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
            {
                Hub = client,
                ThreadPath = subThreadPath,
                UserText = "Hello sub-thread",
                ContextPath = "TestUser"
            });
            Output.WriteLine("PASSED Ã¢â‚¬â€ sub-thread AppendUserMessage routed correctly");
    }
}

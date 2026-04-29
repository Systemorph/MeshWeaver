using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Dedicated tests for routing messages to per-grain hubs whose configuration
/// comes from a satellite NodeType (here: <see cref="ThreadNodeType"/>, satellite
/// path <c>_Thread</c>).
///
/// <para>
/// Goal: isolate the routing-layer round trip from any LLM dependency. We use
/// <see cref="CancelThreadStreamRequest"/> because <see cref="ThreadExecution.AddThreadExecution"/>
/// registers a synchronous handler for it that posts <see cref="CancelThreadStreamResponse"/>
/// straight back via <c>ResponseFor(delivery)</c>. So the test exercises:
/// </para>
/// <list type="number">
///   <item>Client posts to <c>User/TestUser/_Thread/&lt;id&gt;</c> via <see cref="IRoutingService"/>.</item>
///   <item><see cref="RoutingGrain.RouteMessage"/> resolves the path, gets the
///   per-thread <see cref="MessageHubGrain"/>, calls <c>DeliverMessage</c>.</item>
///   <item>The grain's hub (configured by Thread's NodeType
///   <see cref="ThreadNodeType.CreateMeshNode"/> â†’ <c>HubConfiguration</c> â†’
///   <see cref="ThreadExecution.AddThreadExecution"/>) handles the message and
///   posts a response.</item>
///   <item>The response routes back to the client memory stream and the test
///   sees it via <see cref="IMessageHub.Observe{T}"/>.</item>
/// </list>
///
/// <para>
/// If this test fails, the basic "post â†’ per-grain hub â†’ response" round trip
/// is broken. Many of the 17 ailing Orleans tests build on top of this round
/// trip, so a green here is the prerequisite for diagnosing them.
/// </para>
/// </summary>
[Collection(nameof(OrleansClusterCollection))]
public class OrleansHostedHubRoutingTest(SharedOrleansFixture fixture, ITestOutputHelper output)
    : OrleansSharedTestBase(fixture, output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"hostedhubrouting-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// Foundation: the per-thread grain hub answers a request that has a synchronous
    /// handler. Proves "client â†’ Orleans routing â†’ per-grain hub â†’ ResponseFor"
    /// works end-to-end without any LLM, hosted-sub-hub, or watcher in the picture.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task PostToThreadHub_HandlerResponds_RoundTripsViaRouting()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create the Thread node so the per-thread grain has something to activate
        //    against. Use BuildThreadNode (NOT BuildThreadWithMessages) so no auto-execute
        //    fires â€” we only want the hub to come up with the Thread NodeType's
        //    HubConfiguration applied.
        var threadNode = ThreadNodeType.BuildThreadNode("User/TestUser", "Routing test (no LLM)", "TestUser");
        var threadPath = threadNode.Path!;
        Output.WriteLine($"[Setup] Thread path: {threadPath}");

        var createResp = await client.Observe(
                new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("User/TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);

        // 2. Post CancelThreadStreamRequest at the per-thread address. The handler
        //    in ThreadExecution.HandleCancelStream is synchronous and posts the
        //    response back via ResponseFor before returning Processed().
        Output.WriteLine($"[Act] Posting CancelThreadStreamRequest to {threadPath}");
        var response = await client.Observe(
                new CancelThreadStreamRequest { ThreadPath = threadPath },
                o => o.WithTarget(new Address(threadPath)))
            .FirstAsync().ToTask(ct);

        // 3. The response must round-trip with the same ThreadPath the handler stamps
        //    (which is hub.Address.Path, i.e. the same path we posted to).
        response.Should().NotBeNull("response must arrive back at the client");
        response.Message.Should().NotBeNull("response message must deserialize as CancelThreadStreamResponse");
        response.Message.ThreadPath.Should().Be(threadPath,
            "the response is stamped with hub.Address.Path on the silo side");
        Output.WriteLine($"[Assert] Got CancelThreadStreamResponse from {response.Message.ThreadPath}");
    }

    /// <summary>
    /// Cross-grain state propagation: when the per-thread grain receives a request that
    /// writes to its OWN workspace via <c>workspace.UpdateMeshNode(...)</c>, a follow-up
    /// <see cref="GetDataRequest"/> with <see cref="MeshNodeReference"/> from the test
    /// client must observe the new state.
    ///
    /// <para>
    /// We exercise this through <see cref="AppendUserMessageRequest"/> which is registered
    /// on the Thread hub by <see cref="ThreadExecution.AddThreadExecution"/>. The handler
    /// calls <c>UpdateMeshNode</c> to push the new message id onto <see cref="MeshThread.Messages"/>
    /// and then posts a response. After the response arrives, a fresh GetDataRequest must
    /// see the appended message id â€” if it doesn't, the per-grain workspace's
    /// <see cref="MeshNodeReference"/> reducer is not picking up local writes (which is
    /// the suspected root cause of the 17 polling-based test failures).
    /// </para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ThreadHub_LocalWorkspaceWrite_VisibleViaGetDataRequest()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create the Thread (no auto-execute).
        var threadNode = ThreadNodeType.BuildThreadNode("User/TestUser", "Workspace propagation test", "TestUser");
        var threadPath = threadNode.Path!;
        Output.WriteLine($"[Setup] Thread path: {threadPath}");

        var createResp = await client.Observe(
                new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("User/TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);

        // 2. Trigger UpdateMeshNode by posting AppendUserMessageRequest. The handler
        //    writes to workspace.UpdateMeshNode (which lazily forces the workspace
        //    InstanceCollection<MeshNode> to load via MeshNodeTypeSource.InitializeAsync,
        //    then merges the update). The response confirms the write completed.
        var newMsgId = Guid.NewGuid().ToString("N")[..8];
        Output.WriteLine($"[Act] Posting AppendUserMessageRequest with msgId={newMsgId}");
        var resp = await client.Observe(
                new AppendUserMessageRequest
                {
                    ThreadPath = threadPath,
                    UserMessageId = newMsgId,
                    UserText = "Workspace propagation test message",
                    ContextPath = "User/TestUser"
                },
                o => o.WithTarget(new Address(threadPath)))
            .FirstAsync().ToTask(ct);
        resp.Message.Success.Should().BeTrue(resp.Message.Error);
        Output.WriteLine($"[Resp] AppendUserMessageResponse OK");

        // 3. Now read via GetDataRequest. The new message id MUST be visible.
        //    If this fails, the local workspace write is invisible to subsequent
        //    grain-direct reads â€” that's the bug class behind the 17 failures.
        MeshThread? current = null;
        for (var i = 0; i < 20; i++)
        {
            current = await ReadThreadAsync(client, threadPath, ct);
            if (current?.Messages.Contains(newMsgId) == true)
            {
                Output.WriteLine($"[Assert] After {i * 200}ms: Messages has {current.Messages.Count} entries: [{string.Join(",", current.Messages)}]");
                return;
            }
            await Task.Delay(200, ct);
        }
        throw new Xunit.Sdk.XunitException(
            $"After 4s the GetDataRequest for {threadPath} still does not show the appended msgId={newMsgId}. " +
            $"current.Messages={(current == null ? "(null)" : string.Join(",", current.Messages))}. " +
            "The per-thread grain's UpdateMeshNode write did not become visible to a fresh MeshNodeReference read on the same grain.");
    }

    private async Task<MeshThread?> ReadThreadAsync(IMessageHub client, string path, CancellationToken ct)
    {
        var resp = await client.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(path)))
            .FirstAsync().ToTask(ct);
        Output.WriteLine($"[ReadThread] path={path} respType={resp.Message?.GetType().Name ?? "(null msg)"} dataType={resp.Message?.Data?.GetType().Name ?? "(null data)"}");
        var node = resp.Message?.Data as MeshNode;
        if (node == null && resp.Message?.Data is JsonElement je)
        {
            Output.WriteLine($"[ReadThread] data is JsonElement, raw={je.ToString().Substring(0, Math.Min(200, je.ToString().Length))}");
            node = je.Deserialize<MeshNode>(Fixture.ClientMesh.JsonSerializerOptions);
        }
        if (node == null)
        {
            Output.WriteLine($"[ReadThread] node is null after deserialize");
            return null;
        }
        Output.WriteLine($"[ReadThread] node.Path={node.Path} contentType={node.Content?.GetType().Name ?? "(null)"}");
        if (node.Content is MeshThread typed) return typed;
        if (node.Content is JsonElement contentJe)
            return contentJe.Deserialize<MeshThread>(Fixture.ClientMesh.JsonSerializerOptions);
        return null;
    }
}

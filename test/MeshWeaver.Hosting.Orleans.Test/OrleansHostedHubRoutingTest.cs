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
/// <see cref="MeshThread.RequestedCancellationAt flip"/> because <see cref="ThreadExecution.AddThreadExecution"/>
/// registers a synchronous handler for it that posts <see cref="MeshThread.RequestedCancellationAt flip"/>
/// straight back via <c>ResponseFor(delivery)</c>. So the test exercises:
/// </para>
/// <list type="number">
///   <item>Client posts to <c>TestUser/_Thread/&lt;id&gt;</c> via <see cref="IRoutingService"/>.</item>
///   <item><see cref="RoutingGrain.RouteMessage"/> resolves the path, gets the
///   per-thread <see cref="MessageHubGrain"/>, calls <c>DeliverMessage</c>.</item>
///   <item>The grain's hub (configured by Thread's NodeType
///   <see cref="ThreadNodeType.CreateMeshNode"/> ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ <c>HubConfiguration</c> ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢
///   <see cref="ThreadExecution.AddThreadExecution"/>) handles the message and
///   posts a response.</item>
///   <item>The response routes back to the client memory stream and the test
///   sees it via <see cref="IMessageHub.Observe{T}"/>.</item>
/// </list>
///
/// <para>
/// If this test fails, the basic "post ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ per-grain hub ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ response" round trip
/// is broken. Many of the 17 ailing Orleans tests build on top of this round
/// trip, so a green here is the prerequisite for diagnosing them.
/// </para>
/// </summary>
public class OrleansHostedHubRoutingTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"hostedhubrouting-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// Foundation: the per-thread grain hub answers a request that has a synchronous
    /// handler. Proves "client ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ Orleans routing ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ per-grain hub ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ ResponseFor"
    /// works end-to-end without any LLM, hosted-sub-hub, or watcher in the picture.
    /// </summary>
    [Fact]
    public async Task PostToThreadHub_HandlerResponds_RoundTripsViaRouting()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create the Thread node so the per-thread grain has something to activate
        //    against. Use BuildThreadNode (NOT BuildThreadWithMessages) so no auto-execute
        //    fires ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â we only want the hub to come up with the Thread NodeType's
        //    HubConfiguration applied.
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Routing test (no LLM)", "TestUser");
        var threadPath = threadNode.Path!;
        Output.WriteLine($"[Setup] Thread path: {threadPath}");

        var createResp = await client.Observe(
                new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);

        // 2. Post GetDataRequest at the per-thread address â€” generic round-trip
        //    that exercises the same routing layer and returns a response from
        //    the per-thread grain. Replaces the legacy MeshThread.RequestedCancellationAt flip
        //    routing test (cancellation is now stream-update only â€” see
        //    RequestViaStreamUpdate.md).
        Output.WriteLine($"[Act] Posting GetDataRequest to {threadPath}");
        var response = await client.Observe(
                new GetDataRequest(new MeshNodeReference()),
                o => o.WithTarget(new Address(threadPath)))
            .FirstAsync().ToTask(ct);

        // 3. The response must round-trip with a non-null payload (the thread
        //    grain's MeshNodeReference reducer returns the thread MeshNode).
        response.Should().NotBeNull("response must arrive back at the client");
        response.Message.Should().NotBeNull("response message must deserialize");
        response.Message.Data.Should().NotBeNull(
            "the thread grain's MeshNodeReference reducer must return the OWN MeshNode");
        Output.WriteLine("[Assert] Got GetDataResponse from thread grain");
    }

    /// <summary>
    /// Cross-grain state propagation: when the per-thread grain receives a request that
    /// writes to its OWN workspace via <c>workspace.UpdateMeshNode(...)</c>, a follow-up
    /// <see cref="GetDataRequest"/> with <see cref="MeshNodeReference"/> from the test
    /// client must observe the new state.
    ///
    /// <para>
    /// We exercise this through <see cref="ThreadInput.AppendUserInput"/> which is registered
    /// on the Thread hub by <see cref="ThreadExecution.AddThreadExecution"/>. The handler
    /// calls <c>UpdateMeshNode</c> to push the new message id onto <see cref="MeshThread.Messages"/>
    /// and then posts a response. After the response arrives, a fresh GetDataRequest must
    /// see the appended message id ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â if it doesn't, the per-grain workspace's
    /// <see cref="MeshNodeReference"/> reducer is not picking up local writes (which is
    /// the suspected root cause of the 17 polling-based test failures).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ThreadHub_LocalWorkspaceWrite_VisibleViaGetDataRequest()
    {
        var ct = new CancellationTokenSource(20.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create the Thread (no auto-execute).
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Workspace propagation test", "TestUser");
        var threadPath = threadNode.Path!;
        Output.WriteLine($"[Setup] Thread path: {threadPath}");

        var createResp = await client.Observe(
                new CreateNodeRequest(threadNode),
                o => o.WithTarget(new Address("TestUser")))
            .FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);

        // 2. Confirm the thread starts empty.
        var before = await ReadThreadAsync(client, threadPath, ct);
        before.Should().NotBeNull();
        before!.Messages.Count.Should().Be(0, "thread starts empty");

        // 3. Trigger the same submit path the production GUI uses.
        //    ThreadSubmission.Submit posts SubmitMessageRequest; the per-thread
        //    HandleSubmitMessage runs `workspace.UpdateMeshNode(...)` to add
        //    the new user + response ids to MeshThread.Messages. Asserting
        //    on Messages.Count growing is the canary for "local workspace
        //    write visible to grain-direct read" â€” the bug class behind the
        //    polling failures.
        Output.WriteLine("[Act] ThreadSubmission.Submit");
        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "Workspace propagation test message",
            ContextPath = "TestUser"
        });

        // 4. Poll until the new message ids show up via a fresh GetDataRequest.
        MeshThread? current = null;
        for (var i = 0; i < 20; i++)
        {
            current = await ReadThreadAsync(client, threadPath, ct);
            if (current?.Messages.Count > 0)
            {
                Output.WriteLine($"[Assert] After {i * 200}ms: Messages=[{string.Join(",", current.Messages)}]");
                return;
            }
            await Task.Delay(200, ct);
        }
        throw new Xunit.Sdk.XunitException(
            $"After 4s the GetDataRequest for {threadPath} still shows Messages.Count=0. " +
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

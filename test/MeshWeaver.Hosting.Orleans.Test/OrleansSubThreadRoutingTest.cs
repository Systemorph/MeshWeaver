using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// Tests that deeply nested sub-thread paths (delegation pattern) route correctly
/// across multiple path segments in Orleans. Reproduces the production scenario:
///   Parent/_Thread/thread-id/msg-id/sub-thread-id
/// This is 5+ segments deep and requires the RoutingGrain to correctly resolve
/// the path and update the delivery target.
/// </summary>
public class OrleansSubThreadRoutingTest(ITestOutputHelper output) : TestBase(output)
{
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<RlsChatSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<IMessageHub> GetClientAsync()
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "subrouting"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        // Set user identity on the client (simulates Blazor CircuitContext)
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "Roland",
            Name = "Roland Buergi",
            Email = "rbuergi@systemorph.com"
        });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    private async Task<string> CreateNodeAsync(IMessageHub client, MeshNode node, string targetAddress, CancellationToken ct)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, ns={node.Namespace}, path={node.Path}, target={targetAddress}");
        var response = await client.AwaitResponse(
            new CreateNodeRequest(node),
            o => o.WithTarget(new Address(targetAddress)), ct);
        Output.WriteLine($"CreateNodeResponse: success={response.Message.Success}, error={response.Message.Error ?? "(none)"}, path={response.Message.Node?.Path ?? "(null)"}");
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                var ids = content?.Messages ?? [];
                Output.WriteLine($"[Stream] Thread {threadPath}: {ids.Count} message IDs");
                return (IReadOnlyList<string>)ids;
            });
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var nodeId = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(ClientMesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// Simulates the delegation pattern: create a thread, submit a message (creates cells),
    /// then create a sub-thread under the response cell (deeply nested path) and submit to it.
    /// This tests that routing works across 6+ path segments.
    ///
    /// Path hierarchy:
    ///   User/Roland/_Thread/my-thread              (thread, 4 segments)
    ///   User/Roland/_Thread/my-thread/msg1          (message, 5 segments)
    ///   User/Roland/_Thread/my-thread/msg1/sub      (sub-thread, 6 segments)
    ///
    /// The RoutingGrain must resolve the sub-thread grain key correctly and
    /// propagate the access context through the entire chain.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubThreadDelegation_RoutesAcrossMultipleSegments()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create a thread under User/Roland
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Routing test thread", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"Thread: {threadPath}");
        threadPath.Should().StartWith("User/Roland/_Thread/");

        // 2. Submit message to create cells (user msg + response msg)
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Test message for sub-thread routing",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("First SubmitMessageRequest succeeded");

        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // Wait for response to stream and stabilize
        await Task.Delay(2000, ct);

        // 3. Create sub-thread under the response message (deeply nested path)
        var responseMsgId = msgIds[1];
        var parentMsgPath = $"{threadPath}/{responseMsgId}";
        var subThreadId = "sub-thread-routing-test";
        var subThreadPath = $"{parentMsgPath}/{subThreadId}";

        Output.WriteLine($"Creating sub-thread at: {subThreadPath}");
        Output.WriteLine($"  Namespace: {parentMsgPath}");
        Output.WriteLine($"  Segments: {subThreadPath.Split('/').Length}");

        var subThreadNode = new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Sub-thread routing test",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread
            {
                CreatedBy = "Roland"
            }
        };

        var createdSubThreadPath = await CreateNodeAsync(client, subThreadNode, threadPath, ct);
        Output.WriteLine($"Sub-thread created: {createdSubThreadPath}");
        createdSubThreadPath.Should().Be(subThreadPath);

        // 4. Submit message to the sub-thread — this is the critical routing test!
        // The sub-thread is 6 segments deep. The RoutingGrain must resolve this
        // to the correct grain key and propagate access context.
        var subTwoMessages = ObserveThreadMessages(client, subThreadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        Output.WriteLine($"Posting SubmitMessageRequest to sub-thread: {subThreadPath}");
        var subSubmitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = subThreadPath,
                UserMessageText = "Hello from sub-thread!",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(subThreadPath)), ct);

        subSubmitResponse.Message.Success.Should().BeTrue(
            $"Sub-thread SubmitMessage should succeed but got: {subSubmitResponse.Message.Error}");
        Output.WriteLine("Sub-thread SubmitMessageRequest succeeded!");

        // 5. Wait for sub-thread cells to appear
        var subMsgIds = await subTwoMessages;
        subMsgIds.Should().HaveCount(2, "sub-thread should have user message + agent response");
        Output.WriteLine($"Sub-thread message IDs: [{string.Join(", ", subMsgIds)}]");

        // 6. Verify sub-thread user message content
        var subUserMsg = await GetHubContentAsync<ThreadMessage>(
            client, $"{subThreadPath}/{subMsgIds[0]}", ct);
        subUserMsg.Should().NotBeNull("sub-thread user message should exist");
        subUserMsg!.Role.Should().Be("user");
        subUserMsg.Text.Should().Be("Hello from sub-thread!");
        Output.WriteLine($"Sub-thread user cell verified: '{subUserMsg.Text}'");

        // 7. Verify sub-thread response streams (wait for content)
        ThreadMessage? subResponseMsg = null;
        for (var i = 0; i < 30; i++)
        {
            subResponseMsg = await GetHubContentAsync<ThreadMessage>(
                client, $"{subThreadPath}/{subMsgIds[1]}", ct);
            if (!string.IsNullOrEmpty(subResponseMsg?.Text))
                break;
            await Task.Delay(200, ct);
        }
        subResponseMsg.Should().NotBeNull("sub-thread response should exist");
        subResponseMsg!.Role.Should().Be("assistant");
        subResponseMsg.Text.Should().NotBeNullOrEmpty("sub-thread agent should stream a response");
        Output.WriteLine($"Sub-thread response: '{subResponseMsg.Text}'");
    }

    /// <summary>
    /// Verifies that access context propagates correctly when creating and accessing
    /// nodes at deeply nested paths. Uses the real submission flow (SubmitMessage → cells)
    /// to create intermediate nodes, then creates a sub-thread under them.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubThreadCreation_AccessContextPropagates()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create a thread
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Access context test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Submit message to create proper cells (ThreadMessages) via the standard flow
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Access context test msg",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        var msgIds = await twoMessages;
        var responseMsgId = msgIds[1];
        Output.WriteLine($"Response message: {responseMsgId}");

        // Wait for response to stabilize
        await Task.Delay(2000, ct);

        // 3. Create a sub-thread under the response message (deeply nested)
        var parentMsgPath = $"{threadPath}/{responseMsgId}";
        var subThreadId = "access-ctx-test";

        var subThreadNode = new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Access context sub-thread",
            NodeType = ThreadNodeType.NodeType,
            MainNode = "User/Roland",
            Content = new MeshThread { CreatedBy = "Roland" }
        };
        var subThreadPath = await CreateNodeAsync(client, subThreadNode, threadPath, ct);
        Output.WriteLine($"Sub-thread created: {subThreadPath}");
        subThreadPath.Should().Be($"{parentMsgPath}/{subThreadId}");

        // 4. Verify we can read the sub-thread hub content (proves grain activation + access)
        var content = await GetHubContentAsync<MeshThread>(client, subThreadPath, ct);
        content.Should().NotBeNull("sub-thread grain should activate and serve content");
        content!.CreatedBy.Should().Be("Roland");
        Output.WriteLine($"Sub-thread content verified: CreatedBy={content.CreatedBy}");
    }
}

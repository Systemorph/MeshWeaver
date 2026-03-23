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
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Tests thread creation permissions and end-to-end chat flow on Orleans.
/// Mimics the side panel / main panel chat creation flow:
/// 1. Create Organization + Markdown nodes
/// 2. Create a thread under the Markdown node's context (like side panel does)
/// 3. Submit a message and verify cells are pushed
/// 4. Verify streaming response arrives
/// </summary>
public class OrleansThreadAccessTest(ITestOutputHelper output) : TestBase(output)
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
            new Address("client", "threadaccess"),
            config =>
            {
                config.TypeRegistry.AddAITypes();
                return config.AddLayoutClient();
            });
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    /// <summary>
    /// Creates a node via CreateNodeRequest, returns the created path.
    /// </summary>
    private async Task<string> CreateNodeAsync(IMessageHub client, MeshNode node, string targetAddress, CancellationToken ct)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, path={node.Path}, target={targetAddress}");
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
    /// 1. Create an Organization node
    /// 2. Create a Markdown node under it
    /// 3. Create a Thread from the Markdown node's context (mimics side panel)
    /// 4. Verify the thread was created successfully
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateThread_UnderMarkdownNode_Succeeds()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..4];
        // 1. Create Organization — target "User/Roland" (existing hub, routes to silo mesh)
        var orgPath = await CreateNodeAsync(client,
            new MeshNode($"TestOrg{suffix}") { Name = "Test Organization", NodeType = "Markdown" },
            "User/Roland", ct);
        Output.WriteLine($"Organization created: {orgPath}");

        // 2. Create Markdown node under org
        var docPath = await CreateNodeAsync(client,
            new MeshNode($"TestDoc{suffix}", $"TestOrg{suffix}") { Name = "Test Document", NodeType = "Markdown" },
            "User/Roland", ct);
        Output.WriteLine($"Document created: {docPath}");

        // 3. Create Thread under the document context (mimics side panel: CreateNodeRequest to doc address)
        var threadNode = ThreadNodeType.BuildThreadNode(docPath, "Hello from test", "Roland");
        Output.WriteLine($"BuildThreadNode: id={threadNode.Id}, ns={threadNode.Namespace}, path={threadNode.Path}");

        // Target the document address — same as the side panel does
        var threadPath = await CreateNodeAsync(client, threadNode, docPath, ct);
        Output.WriteLine($"Thread created: {threadPath}");

        threadPath.Should().Contain("_Thread/", "thread should be in _Thread satellite partition");

        // 4. Verify Thread content
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull("Thread hub should return Thread content");
        threadContent!.ParentPath.Should().Be(docPath, "thread should reference the parent document");
        Output.WriteLine($"Thread verified: ParentPath={threadContent.ParentPath}, Messages={threadContent.Messages.Count}");
    }

    /// <summary>
    /// Create a thread under User/Roland (mimics side panel with no context).
    /// This should always work since the user has Admin on their own partition.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateThread_UnderUserPartition_Succeeds()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "User thread test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);

        threadPath.Should().StartWith("User/Roland/_Thread/");
        Output.WriteLine($"Thread under user partition: {threadPath}");

        var content = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        content.Should().NotBeNull();
        content!.CreatedBy.Should().Be("Roland");
    }

    /// <summary>
    /// Full chat flow: create thread, submit message, verify cells appear and stream completes.
    /// Mimics what the side panel does when a user types and submits.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubmitChat_FromSidePanel_CellsAppearAndStream()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create thread under user (side panel default when no context)
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Chat flow test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Subscribe to message stream (like ThreadChatView does)
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 3. Submit message (like side panel SendMessageAsync)
        Output.WriteLine("Posting SubmitMessageRequest...");
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello from side panel test",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("SubmitMessageRequest succeeded");

        // 4. Wait for both cells to appear in stream
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user message + agent response");
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 5. Verify user message cell content
        var userMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}", ct);
        userMsg.Should().NotBeNull("user message cell should exist");
        userMsg!.Role.Should().Be("user");
        userMsg.Text.Should().Be("Hello from side panel test");
        userMsg.Type.Should().Be(ThreadMessageType.ExecutedInput);
        Output.WriteLine($"User cell: '{userMsg.Text}'");

        // 6. Verify response cell streams and completes
        ThreadMessage? responseMsg = null;
        var prevLen = 0;
        var stable = 0;
        for (var i = 0; i < 50; i++)
        {
            responseMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}", ct);
            var len = responseMsg?.Text?.Length ?? 0;
            if (len > 0 && len == prevLen && ++stable >= 2) break;
            else stable = 0;
            prevLen = len;
            await Task.Delay(200, ct);
        }

        responseMsg.Should().NotBeNull("response cell should exist");
        responseMsg!.Role.Should().Be("assistant");
        responseMsg.Type.Should().Be(ThreadMessageType.AgentResponse);
        responseMsg.Text.Should().NotBeNullOrEmpty("agent should have streamed a response");
        Output.WriteLine($"Response cell: '{responseMsg.Text}' ({responseMsg.Text.Length} chars)");

        // 7. Verify Thread.Messages list is updated
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull();
        threadContent!.Messages.Should().HaveCount(2);
        threadContent.Messages[0].Should().Be(msgIds[0]);
        threadContent.Messages[1].Should().Be(msgIds[1]);
        Output.WriteLine("Thread.Messages verified");
    }

    /// <summary>
    /// Reproduces the production identity chain failure:
    /// Simulates the Blazor GUI flow where UserContextMiddleware sets CircuitContext
    /// on the portal hub's AccessService, then the component posts SubmitMessageRequest.
    /// Verifies that the user identity propagates through:
    ///   PostPipeline → OrleansRoutingService → MessageHubGrain → AccessControlPipeline
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubmitChat_WithCircuitContext_IdentityPropagates()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;

        // 1. Create client hub simulating a portal circuit
        var client = await GetClientAsync();

        // 2. Set CircuitContext on the client hub — exactly what UserContextMiddleware does
        //    in Blazor after authentication. This is the persistent session identity.
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "Roland",
            Name = "Roland Buergi",
            Email = "rbuergi@systemorph.com"
        });
        Output.WriteLine($"CircuitContext set: {accessService.CircuitContext?.ObjectId}");

        // 3. Verify PostPipeline stamps the delivery with the circuit user (not hub address)
        //    This is what happens when ThreadChatView calls Hub.Post(new SubmitMessageRequest{...})
        var testDelivery = client.Post(
            new SubmitMessageRequest
            {
                ThreadPath = "User/Roland/_Thread/test",
                UserMessageText = "identity check",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(client.Address)); // Target self to capture the delivery
        testDelivery.Should().NotBeNull();
        testDelivery!.AccessContext.Should().NotBeNull("PostPipeline should stamp AccessContext");
        testDelivery.AccessContext!.ObjectId.Should().Be("Roland",
            "PostPipeline should use CircuitContext identity, not hub address");
        Output.WriteLine($"PostPipeline stamped: {testDelivery.AccessContext.ObjectId}");

        // 4. Create thread under user partition (like the GUI does)
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "Identity chain test", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);
        Output.WriteLine($"Thread created: {threadPath}");

        // 5. Subscribe to thread messages (like ThreadChatView subscribes)
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 6. Submit message — this is the critical path that fails in production.
        //    The SubmitMessageRequest has [RequiresPermission(Permission.Thread)].
        //    If identity is lost, AccessControlPipeline rejects with "(anonymous)".
        Output.WriteLine("Posting SubmitMessageRequest with CircuitContext identity...");
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello with identity",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);

        // This is the assertion that fails in production — the submit is rejected
        // with "Access denied: user '(anonymous)' lacks Thread permission"
        submitResponse.Message.Success.Should().BeTrue(
            $"SubmitMessageRequest should succeed with identity 'Roland'. Error: {submitResponse.Message.Error}");
        Output.WriteLine("SubmitMessageRequest succeeded with identity propagation");

        // 7. Verify cells were created (proves the entire chain worked)
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 8. Verify user message has correct content
        var userMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}", ct);
        userMsg.Should().NotBeNull();
        userMsg!.Role.Should().Be("user");
        userMsg.Text.Should().Be("Hello with identity");
        Output.WriteLine($"User cell verified: '{userMsg.Text}'");

        // 9. Wait for response to stream
        ThreadMessage? responseMsg = null;
        var prevLen = 0;
        var stable = 0;
        for (var i = 0; i < 50; i++)
        {
            responseMsg = await GetHubContentAsync<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}", ct);
            var len = responseMsg?.Text?.Length ?? 0;
            if (len > 0 && len == prevLen && ++stable >= 2) break;
            else stable = 0;
            prevLen = len;
            await Task.Delay(200, ct);
        }
        responseMsg.Should().NotBeNull();
        responseMsg!.Text.Should().NotBeNullOrEmpty("streaming should produce a response");
        Output.WriteLine($"Response verified: '{responseMsg.Text}'");
    }

    /// <summary>
    /// Verifies that ThreadMessage nodes (cells) are created as children of the Thread.
    /// In PostgreSQL, these go to the satellite "threads" table.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ThreadMessageNodes_AreChildrenOfThread()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", $"Child node test {Guid.NewGuid():N}", "Roland");
        var threadPath = await CreateNodeAsync(client, threadNode, "User/Roland", ct);

        // Submit message to create child nodes
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Test child nodes",
                ContextPath = "User/Roland"
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        var msgIds = await twoMessages;

        // Verify each message is at {threadPath}/{msgId}
        foreach (var msgId in msgIds)
        {
            var fullPath = $"{threadPath}/{msgId}";
            var msg = await GetHubContentAsync<ThreadMessage>(client, fullPath, ct);
            msg.Should().NotBeNull($"ThreadMessage at {fullPath} should exist");
            msg!.Id.Should().Be(msgId);
            Output.WriteLine($"Child node verified: {fullPath} => role={msg.Role}, type={msg.Type}");
        }
    }
}

/// <summary>
/// Silo configurator that mirrors production: AddGraph + AddAI + AddRowLevelSecurity.
/// RLS enables AccessControlPipeline on node hubs, which checks [RequiresPermission].
/// Without RLS, the pipeline is a no-op and identity issues go unnoticed.
/// </summary>
public class RlsChatSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .AddRowLevelSecurity()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}

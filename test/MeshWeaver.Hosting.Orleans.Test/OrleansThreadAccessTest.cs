using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
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
public class OrleansThreadAccessTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private async Task<IMessageHub> GetClientAsync([CallerMemberName] string? name = null)
        => await base.GetClientAsync($"threadaccess-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// Creates a node via CreateNodeRequest, returns the created path.
    /// </summary>
    private async Task<string> CreateNodeAsync(IMessageHub client, MeshNode node, string targetAddress, CancellationToken ct)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, path={node.Path}, target={targetAddress}");
        var response = await client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress))).FirstAsync().ToTask(ct);
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

    /// <summary>
    /// Reactive single-node content read via <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>
    /// â€” the canonical CQRS path. The stream filters out pre-load empty snapshots
    /// (it is `Where(change => change.Value != null).Select(...)` internally), so
    /// the first emission always carries content. No GetDataRequest single-shot,
    /// no FirstAsync trap on a transient empty initial snapshot.
    /// </summary>
    private IObservable<T?> GetHubContent<T>(IMessageHub client, string path) where T : class
    {
        var workspace = client.GetWorkspace();
        return workspace.GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is T typed) return typed;
                if (node?.Content is JsonElement contentJe)
                    return contentJe.Deserialize<T>(Fixture.ClientMesh.JsonSerializerOptions);
                return null;
            });
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
        // 1. Create Organization under TestUser â€” this is where TestUser has Admin
        // (via the seeded Public_Access AccessAssignment with MainNode = "User"). A
        // root-level path like "TestOrg{suffix}" would fail the RLS Create check
        // because Public_Access is scoped to "User/*" only.
        var orgPath = await CreateNodeAsync(client,
            new MeshNode($"TestOrg{suffix}", "TestUser") { Name = "Test Organization", NodeType = "Markdown" },
            "TestUser", ct);
        Output.WriteLine($"Organization created: {orgPath}");

        // 2. Create Markdown node under org
        var docPath = await CreateNodeAsync(client,
            new MeshNode($"TestDoc{suffix}", orgPath) { Name = "Test Document", NodeType = "Markdown" },
            "TestUser", ct);
        Output.WriteLine($"Document created: {docPath}");

        // 3. Create Thread under the document context (mimics side panel: CreateNodeRequest to doc address)
        var threadNode = ThreadNodeType.BuildThreadNode(docPath, "Hello from test", "TestUser");
        Output.WriteLine($"BuildThreadNode: id={threadNode.Id}, ns={threadNode.Namespace}, path={threadNode.Path}");

        // Target the document address Ã¢â‚¬â€ same as the side panel does
        var threadPath = await CreateNodeAsync(client, threadNode, docPath, ct);
        Output.WriteLine($"Thread created: {threadPath}");

        threadPath.Should().Contain("_Thread/", "thread should be in _Thread satellite partition");

        // 4. Verify Thread content
        var threadContent = await GetHubContent<MeshThread>(client, threadPath).Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        threadContent.Should().NotBeNull("Thread hub should return Thread content");
        Output.WriteLine($"Thread verified: Messages={threadContent!.Messages.Count}");
    }

    /// <summary>
    /// Create a thread under TestUser (mimics side panel with no context).
    /// This should always work since the user has Admin on their own partition.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateThread_UnderUserPartition_Succeeds()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "User thread test", "TestUser");
        var threadPath = await CreateNodeAsync(client, threadNode, "TestUser", ct);

        threadPath.Should().StartWith("TestUser/_Thread/");
        Output.WriteLine($"Thread under user partition: {threadPath}");

        var content = await GetHubContent<MeshThread>(client, threadPath).Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        content.Should().NotBeNull();
        content!.CreatedBy.Should().Be("TestUser");
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
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Chat flow test", "TestUser");
        var threadPath = await CreateNodeAsync(client, threadNode, "TestUser", ct);
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Subscribe to message stream (like ThreadChatView does)
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 3. Submit message via AppendUserMessageRequest (like side panel SendMessageAsync)
        Output.WriteLine("Posting AppendUserMessageRequest...");
        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
            {
                Hub = client,
                ThreadPath = threadPath,
                UserText = "Hello from side panel test",
                ContextPath = "TestUser"
            });
            Output.WriteLine("AppendUserMessageRequest succeeded");

        // 4. Wait for both cells to appear in stream
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user message + agent response");
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 5. Verify user message cell content
        var userMsg = await GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}").Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
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
            responseMsg = await GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}").Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
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
        var threadContent = await GetHubContent<MeshThread>(client, threadPath).Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        threadContent.Should().NotBeNull();
        threadContent!.Messages.Should().HaveCount(2);
        threadContent.Messages[0].Should().Be(msgIds[0]);
        threadContent.Messages[1].Should().Be(msgIds[1]);
        Output.WriteLine("Thread.Messages verified");
    }

    /// <summary>
    /// Reproduces the production identity chain failure:
    /// Simulates the Blazor GUI flow where UserContextMiddleware sets CircuitContext
    /// on the portal hub's AccessService, then the component posts AppendUserMessageRequest.
    /// Verifies that the user identity propagates through:
    ///   PostPipeline Ã¢â€ â€™ OrleansRoutingService Ã¢â€ â€™ MessageHubGrain Ã¢â€ â€™ AccessControlPipeline
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubmitChat_WithCircuitContext_IdentityPropagates()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;

        // 1. Create client hub simulating a portal circuit
        var client = await GetClientAsync();

        // 2. Set CircuitContext on the client hub Ã¢â‚¬â€ exactly what UserContextMiddleware does
        //    in Blazor after authentication. This is the persistent session identity.
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "TestUser",
            Name = "Test User",
            Email = "testuser@meshweaver.io"
        });
        Output.WriteLine($"CircuitContext set: {accessService.CircuitContext?.ObjectId}");

        // 3. Create thread under user partition (like the GUI does)
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", $"Identity chain test {Guid.NewGuid():N}", "TestUser");
        var threadPath = await CreateNodeAsync(client, threadNode, "TestUser", ct);
        Output.WriteLine($"Thread created: {threadPath}");

        // 5. Subscribe to thread messages (like ThreadChatView subscribes)
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 6. Submit message - critical access-control path.
        //    ThreadInput.AppendUserInput -> UpdateRemote -> owning per-thread
        //    grain's MeshNodeReference reducer write. The
        //    [SubmitMessagePermission] check now sits on the data-change
        //    pipeline; if identity is lost, the per-thread grain rejects the
        //    write with "(anonymous)" and Messages stays empty.
        Output.WriteLine("ThreadSubmission.Submit with CircuitContext identity...");
        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "Hello with identity",
            ContextPath = "TestUser"
        });

        // 6. Wait for both cells to appear in stream
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user message + agent response");
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 7. Verify user message cell content
        var userMsg = await GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}").Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
        userMsg.Should().NotBeNull("user message cell should exist");
        userMsg!.Role.Should().Be("user");
        userMsg.Text.Should().Be("Hello with identity");
        Output.WriteLine($"User cell verified: '{userMsg.Text}'");

        // 8. Wait for response to stream and complete
        ThreadMessage? responseMsg = null;
        var prevLen = 0;
        var stable = 0;
        for (var i = 0; i < 50; i++)
        {
            responseMsg = await GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}").Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
            var len = responseMsg?.Text?.Length ?? 0;
            if (len > 0 && len == prevLen && ++stable >= 2) break;
            else stable = 0;
            prevLen = len;
            await Task.Delay(200, ct);
        }
        responseMsg.Should().NotBeNull("response cell should exist");
        responseMsg!.Role.Should().Be("assistant");
        responseMsg.Text.Should().NotBeNullOrEmpty("streaming should produce a response");
        Output.WriteLine($"Response verified: '{responseMsg.Text}'");
    }

    /// <summary>
    /// Verifies that when a user lacks Thread permission, AppendUserMessageRequest
    /// returns a clear DeliveryFailure error Ã¢â‚¬â€ NOT a silent timeout/hang.
    /// Uses Viewer role which has Read+Execute but NOT Thread.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task SubmitChat_WithoutThreadPermission_ReturnsError()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // Set CircuitContext as a Viewer user (no Thread permission)
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "ViewerUser",
            Name = "Viewer Only"
        });

        // Create thread (Thread permission maps to Thread, Viewer doesn't have it)
        // But first we need the thread to exist Ã¢â‚¬â€ create with a privileged context
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "TestUser",
            Name = "TestUser"
        });
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser",
            $"Error test {Guid.NewGuid():N}", "TestUser");
        var threadPath = await CreateNodeAsync(client, threadNode, "TestUser", ct);
        Output.WriteLine($"Thread created: {threadPath}");

        // Switch to unprivileged user
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "ViewerUser",
            Name = "Viewer Only"
        });

        // Submit message Ã¢â‚¬â€ should fail with a clear error, not hang
        // Submit via SubmitContext.OnError - same callback the production GUI
        // uses (ThreadChatView). If the permission check fails the OnError
        // callback fires with the rejection message and the test does NOT hang.
        var responseTcs = new TaskCompletionSource<string?>();
        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "Should be denied",
            ContextPath = "TestUser",
            OnError = msg => responseTcs.TrySetResult(msg)
        });

        var timeoutTask = Task.Delay(15_000, ct);
        var error = await Task.WhenAny(responseTcs.Task, timeoutTask) == responseTcs.Task
            ? await responseTcs.Task
            : "TIMEOUT: No error response received Ã¢â‚¬â€ UI would hang silently!";

        Output.WriteLine($"Error response: {error}");
        error.Should().NotBeNull("should receive an error, not succeed");
        error.Should().NotStartWith("TIMEOUT", "error response must arrive promptly, not hang");
        error.Should().Contain("Thread", "error message should mention the missing permission");
        Output.WriteLine("Error message test passed: user gets clear feedback on permission denial");
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

        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", $"Child node test {Guid.NewGuid():N}", "TestUser");
        var threadPath = await CreateNodeAsync(client, threadNode, "TestUser", ct);

        // Submit message to create child nodes
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        MeshWeaver.AI.ThreadSubmission.Submit(new MeshWeaver.AI.SubmitContext
            {
                Hub = client,
                ThreadPath = threadPath,
                UserText = "Test child nodes",
                ContextPath = "TestUser"
            });
            var msgIds = await twoMessages;

        // Verify each message is at {threadPath}/{msgId}
        foreach (var msgId in msgIds)
        {
            var fullPath = $"{threadPath}/{msgId}";
            var msg = await GetHubContent<ThreadMessage>(client, fullPath).Where(c => c is not null).Take(1).Timeout(30.Seconds()).FirstAsync().ToTask(ct);
            msg.Should().NotBeNull($"ThreadMessage at {fullPath} should exist");
            Output.WriteLine($"Child node verified: {fullPath} => role={msg.Role}, type={msg.Type}");
        }
    }
}

/// <summary>
/// Silo configurator that mirrors production: AddGraph + AddAI + AddRowLevelSecurity.
/// RLS enables AccessControlPipeline on node hubs, which checks [RequiresPermission].
/// Without RLS, the pipeline is a no-op and identity issues go unnoticed.
/// Pre-seeds Public Admin access so authenticated users can create/manage nodes.
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
            // Pre-seed sample users and Public Admin access (same as MonolithMeshTestBase)
            .AddMeshNodes(
                new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" })
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Creates Public Admin access on the User partition. The AccessAssignment
    /// node MUST live in a namespace ending in "/_Access" â€” SecurityService.
    /// ComputeScopeRoles drops anything else from the scopeâ†’roles map.
    /// </summary>
    private static MeshNode[] PublicEditorAccess()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "Public",
            DisplayName = "Public",
            Roles = [new RoleAssignment { Role = "Admin" }]
        };
        return
        [
            new("Public_Access", "User/_Access")
            {
                NodeType = "AccessAssignment",
                Name = "Public Access",
                Content = assignment,
                MainNode = "User",
            }
        ];
    }
}

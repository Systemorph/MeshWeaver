using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
///
/// 🚨 Tests are <c>void</c> + reactive assertions (no <c>async</c>/<c>await</c>):
/// blocking inside an async test deadlocks the in-process hub scheduler — the
/// agent's streaming execution shares the process and its continuations are
/// starved by the captured async SynchronizationContext. See
/// FluentAssertionsToReactive.md §2a + ObservableAssertions remarks.
/// </summary>
public class OrleansThreadAccessTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    // Synchronous client acquisition — await-free test bodies resolve the client
    // hub once up front on the plain xUnit thread (no async SynchronizationContext
    // to starve the in-process hub scheduler). All hub-reachable waits live inside
    // .Should() blocking assertions afterward.
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"threadaccess-{name}-{Guid.NewGuid():N}", "TestUser");

    /// <summary>
    /// Creates a node via CreateNodeRequest, returns the created path.
    /// </summary>
    private string CreateNode(IMessageHub client, MeshNode node, string targetAddress)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, path={node.Path}, target={targetAddress}");
        var response = client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress)))
            .Should().Within(30.Seconds()).Emit();
        Output.WriteLine($"CreateNodeResponse: success={response.Message.Success}, error={response.Message.Error ?? "(none)"}, path={response.Message.Node?.Path ?? "(null)"}");
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
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
    /// Reactive single-node content read via the canonical
    /// <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace, string)"/>
    /// path. The stream filters out pre-load empty snapshots, so the first
    /// content-bearing emission carries the node. Assert with <c>.Should().Match(...)</c>.
    /// </summary>
    private static IObservable<T?> GetHubContent<T>(IMessageHub client, string path) where T : class
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is T typed) return typed;
                if (node?.Content is JsonElement contentJe)
                    return contentJe.Deserialize<T>(client.JsonSerializerOptions);
                return null;
            });

    /// <summary>
    /// 1. Create an Organization node
    /// 2. Create a Markdown node under it
    /// 3. Create a Thread from the Markdown node's context (mimics side panel)
    /// 4. Verify the thread was created successfully
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateThread_UnderMarkdownNode_Succeeds()
    {
        var client = GetClient();
        var suffix = Guid.NewGuid().ToString("N")[..4];
        // 1. Create Organization under TestUser — this is where TestUser has Admin
        // (via the seeded Public_Access AccessAssignment with MainNode = "User"). A
        // root-level path like "TestOrg{suffix}" would fail the RLS Create check
        // because Public_Access is scoped to "User/*" only.
        var orgPath = CreateNode(client,
            new MeshNode($"TestOrg{suffix}", "TestUser") { Name = "Test Organization", NodeType = "Markdown" },
            "TestUser");
        Output.WriteLine($"Organization created: {orgPath}");

        // 2. Create Markdown node under org
        var docPath = CreateNode(client,
            new MeshNode($"TestDoc{suffix}", orgPath) { Name = "Test Document", NodeType = "Markdown" },
            "TestUser");
        Output.WriteLine($"Document created: {docPath}");

        // 3. Create Thread under the document context (mimics side panel: CreateNodeRequest to doc address)
        var threadNode = ThreadNodeType.BuildThreadNode(docPath, "Hello from test", "TestUser");
        Output.WriteLine($"BuildThreadNode: id={threadNode.Id}, ns={threadNode.Namespace}, path={threadNode.Path}");

        // Target the document address — same as the side panel does
        var threadPath = CreateNode(client, threadNode, docPath);
        Output.WriteLine($"Thread created: {threadPath}");

        threadPath.Should().Contain("_Thread/", "thread should be in _Thread satellite partition");

        // 4. Verify Thread content
        var threadContent = GetHubContent<MeshThread>(client, threadPath)
            .Should().Within(30.Seconds()).Match(c => c is not null);
        Output.WriteLine($"Thread verified: Messages={threadContent!.Messages.Count}");
    }

    /// <summary>
    /// Create a thread under TestUser (mimics side panel with no context).
    /// This should always work since the user has Admin on their own partition.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateThread_UnderUserPartition_Succeeds()
    {
        var client = GetClient();

        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "User thread test", "TestUser");
        var threadPath = CreateNode(client, threadNode, "TestUser");

        threadPath.Should().StartWith("TestUser/_Thread/");
        Output.WriteLine($"Thread under user partition: {threadPath}");

        var content = GetHubContent<MeshThread>(client, threadPath)
            .Should().Within(30.Seconds()).Match(c => c is not null);
        content!.CreatedBy.Should().Be("TestUser");
    }

    /// <summary>
    /// Full chat flow: create thread, submit message, verify cells appear and stream completes.
    /// Mimics what the side panel does when a user types and submits.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SubmitChat_FromSidePanel_CellsAppearAndStream()
    {
        var client = GetClient();

        // 1. Create thread under user (side panel default when no context)
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "Chat flow test", "TestUser");
        var threadPath = CreateNode(client, threadNode, "TestUser");
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Submit message via workspace extension (like side panel SendMessageAsync)
        Output.WriteLine("Posting SubmitMessage...");
        client.SubmitMessage(
            threadPath,
            "Hello from side panel test",
            contextPath: "TestUser");
        Output.WriteLine("SubmitMessage succeeded");

        // 3. Wait for both cells to appear in stream (like ThreadChatView does)
        var msgIds = ObserveThreadMessages(client, threadPath)
            .Should().Within(45.Seconds()).Match(ids => ids.Count >= 2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 4. Verify user message cell content
        var userMsg = GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}")
            .Should().Within(30.Seconds()).Match(c => c is not null);
        userMsg!.Role.Should().Be("user");
        userMsg.Text.Should().Be("Hello from side panel test");
        userMsg.Type.Should().Be(ThreadMessageType.ExecutedInput);
        Output.WriteLine($"User cell: '{userMsg.Text}'");

        // 5. Verify response cell streams and completes (non-empty text).
        var responseMsg = GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}")
            .Should().Within(45.Seconds()).Match(m => !string.IsNullOrEmpty(m?.Text));
        responseMsg!.Role.Should().Be("assistant");
        responseMsg.Type.Should().Be(ThreadMessageType.AgentResponse);
        responseMsg.Text.Should().NotBeNullOrEmpty("agent should have streamed a response");
        Output.WriteLine($"Response cell: '{responseMsg.Text}' ({responseMsg.Text!.Length} chars)");

        // 6. Verify Thread.Messages list is updated
        var threadContent = GetHubContent<MeshThread>(client, threadPath)
            .Should().Within(30.Seconds()).Match(c => c is { Messages.Count: >= 2 });
        threadContent!.Messages.Should().HaveCount(2);
        threadContent.Messages[0].Should().Be(msgIds[0]);
        threadContent.Messages[1].Should().Be(msgIds[1]);
        Output.WriteLine("Thread.Messages verified");
    }

    /// <summary>
    /// Reproduces the production identity chain failure:
    /// Simulates the Blazor GUI flow where UserContextMiddleware sets CircuitContext
    /// on the portal hub's AccessService, then the component submits user input.
    /// Verifies that the user identity propagates through:
    ///   PostPipeline -> OrleansRoutingService -> MessageHubGrain -> AccessControlPipeline
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SubmitChat_WithCircuitContext_IdentityPropagates()
    {
        // 1. Create client hub simulating a portal circuit
        var client = GetClient();

        // 2. Set CircuitContext on the client hub — exactly what UserContextMiddleware does
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
        var threadPath = CreateNode(client, threadNode, "TestUser");
        Output.WriteLine($"Thread created: {threadPath}");

        // 4. Submit message - critical access-control path.
        //    ThreadInput.AppendUserInput -> UpdateRemote -> owning per-thread
        //    grain's MeshNodeReference reducer write. The submit permission check
        //    sits on the data-change pipeline; if identity is lost, the per-thread
        //    grain rejects the write with "(anonymous)" and Messages stays empty.
        Output.WriteLine("SubmitMessage with CircuitContext identity...");
        client.SubmitMessage(
            threadPath,
            "Hello with identity",
            contextPath: "TestUser");

        // 5. Wait for both cells to appear in stream
        var msgIds = ObserveThreadMessages(client, threadPath)
            .Should().Within(45.Seconds()).Match(ids => ids.Count >= 2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 6. Verify user message cell content
        var userMsg = GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[0]}")
            .Should().Within(30.Seconds()).Match(c => c is not null);
        userMsg!.Role.Should().Be("user");
        userMsg.Text.Should().Be("Hello with identity");
        Output.WriteLine($"User cell verified: '{userMsg.Text}'");

        // 7. Wait for response to stream and complete
        var responseMsg = GetHubContent<ThreadMessage>(client, $"{threadPath}/{msgIds[1]}")
            .Should().Within(45.Seconds()).Match(m => !string.IsNullOrEmpty(m?.Text));
        responseMsg!.Role.Should().Be("assistant");
        responseMsg.Text.Should().NotBeNullOrEmpty("streaming should produce a response");
        Output.WriteLine($"Response verified: '{responseMsg.Text}'");
    }

    /// <summary>
    /// Verifies that when a user lacks Thread permission, the submission is rejected
    /// by the per-thread grain's AccessControlPipeline — the message is NEVER ingested
    /// into Messages — rather than silently succeeding. Uses ViewerUser which has no
    /// access assignment, so the cross-hub stream.Update write is denied.
    /// <para>
    /// The denial happens asynchronously on the owning grain (the cross-hub
    /// <c>PatchDataRequest</c> is rejected by AccessControlPipeline), so it cannot
    /// surface synchronously through <c>SubmitMessage(onError:)</c> — that callback
    /// only fires when <c>AppendUserInput</c> throws inline. The observable signal a
    /// test CAN assert on is the negative one: the thread's <c>Messages</c> never
    /// gains the user message. A passing submission would push 2 cells within ~2 s;
    /// we assert no cell ever appears in a generous window, proving the write was
    /// denied and the UI doesn't silently accept a forbidden message.
    /// </para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public void SubmitChat_WithoutThreadPermission_ReturnsError()
    {
        var client = GetClient();
        var accessService = client.ServiceProvider.GetRequiredService<AccessService>();

        // Create the thread with a privileged context first (TestUser is Admin).
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "TestUser",
            Name = "TestUser"
        });
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser",
            $"Error test {Guid.NewGuid():N}", "TestUser");
        var threadPath = CreateNode(client, threadNode, "TestUser");
        Output.WriteLine($"Thread created: {threadPath}");

        // Switch to unprivileged user (no Thread permission — no access assignment).
        accessService.SetCircuitContext(new AccessContext
        {
            ObjectId = "ViewerUser",
            Name = "Viewer Only"
        });

        // Submit message — the per-thread grain's AccessControlPipeline must reject
        // the cross-hub write. onError captures any SYNCHRONOUS failure (none is
        // expected here — the rejection is async on the owner grain).
        string? syncError = null;
        client.SubmitMessage(
            threadPath,
            "Should be denied",
            contextPath: "TestUser",
            onError: msg => syncError = msg);

        // The load-bearing assertion: a denied submission NEVER produces message
        // cells. A permitted submission would push the user cell within ~2 s; we
        // assert no cell appears in a generous window. If the permission check were
        // bypassed (identity lost / pipeline misrouted), a cell WOULD appear here
        // and this assertion would fail — exactly the regression we guard against.
        ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 1)
            .Should().NotEmit(within: 8.Seconds(),
                because: "a user lacking Thread permission must NOT get the message ingested — " +
                         "the per-thread grain's AccessControlPipeline rejects the write");

        Output.WriteLine($"Submission denied as expected (no cells ingested). syncError={syncError ?? "(none — async denial)"}");
    }

    /// <summary>
    /// Verifies that ThreadMessage nodes (cells) are created as children of the Thread.
    /// In PostgreSQL, these go to the satellite "threads" table.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ThreadMessageNodes_AreChildrenOfThread()
    {
        var client = GetClient();

        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", $"Child node test {Guid.NewGuid():N}", "TestUser");
        var threadPath = CreateNode(client, threadNode, "TestUser");

        client.SubmitMessage(
            threadPath,
            "Test child nodes",
            contextPath: "TestUser");

        var msgIds = ObserveThreadMessages(client, threadPath)
            .Should().Within(45.Seconds()).Match(ids => ids.Count >= 2);

        // Verify each message is at {threadPath}/{msgId}
        foreach (var msgId in msgIds)
        {
            var fullPath = $"{threadPath}/{msgId}";
            var msg = GetHubContent<ThreadMessage>(client, fullPath)
                .Should().Within(30.Seconds()).Match(c => c is not null);
            Output.WriteLine($"Child node verified: {fullPath} => role={msg!.Role}, type={msg.Type}");
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
    /// node MUST live in a namespace ending in "/_Access" — SecurityService.
    /// ComputeScopeRoles drops anything else from the scope→roles map.
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

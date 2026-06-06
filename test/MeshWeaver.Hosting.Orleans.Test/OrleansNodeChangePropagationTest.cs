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
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end Orleans test for NodeChangeEntry propagation through delegation chains.
///
/// Exercises the FULL production flow:
/// 1. Client creates a thread (like ThreadChatView.SendMessageAsync)
/// 2. Client submits a message (ThreadInput.AppendUserInput Ã¢â€ â€™ thread grain)
/// 3. Thread grain creates user + response cells via Observable
/// 4. Execution starts on _Exec hosted hub (streaming loop via InvokeAsync)
/// 5. Top-level agent calls Create tool (MeshPlugin) Ã¢â€ â€™ NodeChangeEntry generated
/// 6. Top-level agent delegates to sub-agent Ã¢â€ â€™ sub-thread created, SubmitMessage posted
/// 7. Sub-agent calls Patch tool Ã¢â€ â€™ NodeChangeEntry generated in sub-thread
/// 8. Sub-thread completes Ã¢â€ â€™ server-internal SubmitMessageResponse.UpdatedNodes propagates up
/// 9. Parent merges node changes via ForwardNodeChange Ã¢â€ â€™ aggregated with min/max versions
/// 10. Parent completes Ã¢â€ â€™ final NodeChangeEntry list on response message
///
/// This test specifically validates:
/// - No deadlocks in the delegation chain (execution hub, TCS resolution, callbacks)
/// - Access context propagation through all hops
/// - NodeChangeEntry aggregation across delegation boundaries
/// - Correct routing of deeply nested sub-thread paths in Orleans
/// </summary>
public class OrleansNodeChangePropagationTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub GetClient([CallerMemberName] string? name = null)
        => base.GetClient($"nodechange-{name}-{Guid.NewGuid():N}", "TestUser");

    private string CreateNode(IMessageHub client, MeshNode node, string targetAddress)
    {
        Output.WriteLine($"CreateNodeRequest: id={node.Id}, path={node.Path}, target={targetAddress}");
        var response = client.Observe(new CreateNodeRequest(node), o => o.WithTarget(new Address(targetAddress)))
            .Should().Within(45.Seconds()).Emit();
        Output.WriteLine($"CreateNodeResponse: success={response.Message.Success}, error={response.Message.Error ?? "(none)"}, path={response.Message.Node?.Path ?? "(null)"}, nodeType={response.Message.Node?.NodeType ?? "(null)"}");
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetMeshNodeStream(threadPath)
            .Select(node =>
            {
                
                var content = node?.Content as MeshThread;
                var ids = content?.Messages ?? [];
                Output.WriteLine($"[Stream] Thread {threadPath}: {ids.Count} message IDs");
                return (IReadOnlyList<string>)ids;
            });
    }

    // Canonical CQRS-correct LIVE read via the per-node MeshNode stream. Returns an
    // IObservable<T?> the caller asserts on with .Should().Within(...).Match(...) —
    // no one-shot GetDataRequest, no await, no poll loop.
    private IObservable<T?> GetContent<T>(IMessageHub client, string path) where T : class
        => client.GetWorkspace().GetMeshNodeStream(path)
            .Select(node =>
            {
                if (node?.Content is T typed) return typed;
                if (node?.Content is JsonElement contentJe)
                    return contentJe.Deserialize<T>(Fixture.ClientMesh.JsonSerializerOptions);
                return null;
            });

    /// <summary>
    /// Full chain: top agent calls Create Ã¢â€ â€™ delegates Ã¢â€ â€™ sub-agent calls Patch Ã¢â€ â€™ NodeChangeEntry propagates.
    /// Tests for deadlocks: the execution hub (InvokeAsync) blocks during streaming;
    /// delegation TCS resolution must not require the blocked scheduler.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Delegation_NodeChanges_PropagateFromSubThread()
    {
        // Pull IMessageHub from the silo's container â€” ChatClientAgentFactory needs it
        // so MeshPlugin tools (Create/Patch/delegate_to_agent) get wired into every test agent.
        var siloHub = ((InProcessSiloHandle)Fixture.Cluster.Silos[0]).SiloHost.Services
            .GetRequiredService<IMessageHub>();
        SharedOrleansFixture.SwappableFactory.SetInner(new NodeChangeTestChatClientFactory(siloHub));
        try
        {
        var client = GetClient();

        // 1. Create thread Ã¢â‚¬â€ exactly like ThreadChatView.SendMessageAsync does
        var threadNode = ThreadNodeType.BuildThreadNode("TestUser", "NodeChange propagation test", "TestUser");
        var threadPath = CreateNode(client, threadNode, "TestUser");
        Output.WriteLine($"Thread created: {threadPath}");

        // 2. Messages observed reactively after submit (live replaying stream).

        // 3. Submit message Ã¢â‚¬â€ triggers the ToolCallDelegatingChatClient which:
        //    Turn 1: calls Create (creates a Markdown node)
        //    Turn 2: calls delegate_to_agent (Executor)
        //    Turn 3: returns summary text after delegation completes
        Output.WriteLine("Posting ThreadInput.AppendUserInput (Create + Delegate chain)...");
        client.SubmitMessage(
            threadPath,
            "Create a doc and delegate updates to Executor",
            contextPath: "TestUser");
            Output.WriteLine("ThreadInput.AppendUserInput succeeded Ã¢â‚¬â€ submission queued");

        // 4. Wait for message IDs (live replaying stream — observe after submit)
        var msgIds = ObserveThreadMessages(client, threadPath)
            .Should().Within(45.Seconds()).Match(ids => ids.Count >= 2);
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Message IDs: [{string.Join(", ", msgIds)}]");

        // 5. Wait for execution to complete Ã¢â‚¬â€ poll response message
        //    If the delegation chain deadlocks, this times out.
        var responsePath = $"{threadPath}/{msgIds[1]}";
        // Wait until the response cell has tool calls AND text AND the propagated
        // node change. UpdatedNodes is populated by SEPARATE async writes from the
        // terminal-text write: the sub-thread's Patch reaches the parent via
        // client.ForwardNodeChange (ThreadExecution.cs:915), which can fire AFTER
        // the snapshot that first satisfies ToolCalls+Text (see the
        // "UpdateDelegationStatus can still fire after the await foreach exits"
        // note at ThreadExecution.cs:1361). Waiting only on ToolCalls+Text and then
        // asserting UpdatedNodes on that captured snapshot races the propagation and
        // intermittently observes an empty list — so wait on the actual asserted
        // state (the doc change present) instead of a proxy.
        var responseMsg = GetContent<ThreadMessage>(client, responsePath)
            .Should().Within(45.Seconds()).Match(m =>
                m?.ToolCalls is { Count: >= 2 }
                && !string.IsNullOrEmpty(m.Text)
                && m.UpdatedNodes.Any(e => e.Path.Contains("test-doc-nodechange")));
        Output.WriteLine($"Response: text len={responseMsg!.Text?.Length ?? 0}, toolCalls={responseMsg.ToolCalls.Count}, updatedNodes={responseMsg.UpdatedNodes.Count}");

        // 6. Verify response message has tool calls
        responseMsg.Should().NotBeNull("response message should exist after execution");
        responseMsg!.ToolCalls.Should().NotBeEmpty("agent should have made tool calls");

        var createCall = responseMsg.ToolCalls.FirstOrDefault(t => t.Name == "Create");
        createCall.Should().NotBeNull("agent should have called Create tool");
        createCall!.IsSuccess.Should().BeTrue("Create tool should succeed");
        Output.WriteLine($"Create tool call: success={createCall.IsSuccess}, args={createCall.Arguments?[..Math.Min(60, createCall.Arguments?.Length ?? 0)]}");

        var delegateCall = responseMsg.ToolCalls.FirstOrDefault(t => t.Name?.Contains("delegate") == true);
        delegateCall.Should().NotBeNull("agent should have called delegate_to_agent");
        delegateCall!.DelegationPath.Should().NotBeNullOrEmpty("delegation should have a sub-thread path");
        Output.WriteLine($"Delegation: path={delegateCall.DelegationPath}, success={delegateCall.IsSuccess}");

        // 7. Verify the Markdown node was created by the Create tool.
        // Use the silo's workspace-side MeshNodeStream (NOT Query) — per
        // `feedback_cqrs_no_query_for_content.md`: Query is eventually-
        // consistent against an in-memory index that's separate from the
        // storage adapter's commit. After 27 in-flight DataChangeRequests
        // stack up on the response message hub during delegation streaming,
        // the index update can lag past the 10 s budget — the storage value
        // is already there but the query stream hasn't emitted Added yet.
        // GetMeshNodeStream reads the authoritative per-node stream directly.
        var siloWorkspace = siloHub.GetWorkspace();
        var createdNode = siloWorkspace
            .GetMeshNodeStream("TestUser/test-doc-nodechange")
            .Should().Within(10.Seconds()).Match(n => n is not null);
        createdNode.Should().NotBeNull("Create tool should have created the Markdown node");
        Output.WriteLine($"Created node: {createdNode.Path}, name={createdNode.Name}");

        // 8. Verify sub-thread exists and completed
        var subThreadPath = delegateCall.DelegationPath!;
        var subThread = GetContent<MeshThread>(client, subThreadPath)
            .Should().Within(45.Seconds()).Match(t => (t?.Messages.Count ?? 0) >= 2);
        subThread.Should().NotBeNull("sub-thread should exist");
        subThread!.Messages.Should().HaveCount(2, "sub-thread should have user + response messages");
        Output.WriteLine($"Sub-thread: {subThreadPath}, messages={subThread.Messages.Count}");

        // 9. Verify sub-thread response has Patch tool call
        var subResponsePath = $"{subThreadPath}/{subThread.Messages[1]}";
        var subResponseMsg = GetContent<ThreadMessage>(client, subResponsePath)
            .Should().Within(45.Seconds()).Match(m => m?.ToolCalls is { Count: > 0 });
        subResponseMsg.Should().NotBeNull("sub-thread response should exist");
        var patchCall = subResponseMsg!.ToolCalls.FirstOrDefault(t => t.Name == "Patch");
        patchCall.Should().NotBeNull("sub-agent should have called Patch tool");
        Output.WriteLine($"Sub-thread Patch: success={patchCall!.IsSuccess}, args={patchCall.Arguments?[..Math.Min(60, patchCall.Arguments?.Length ?? 0)]}");

        // 10. Verify NodeChangeEntry propagated to parent response
        responseMsg.UpdatedNodes.Should().NotBeEmpty(
            "parent response should have aggregated UpdatedNodes from both Create and sub-thread Patch");
        Output.WriteLine($"UpdatedNodes on parent response: {responseMsg.UpdatedNodes.Count} entries");
        foreach (var entry in responseMsg.UpdatedNodes)
            Output.WriteLine($"  {entry.Operation}: {entry.Path} v{entry.VersionBefore}Ã¢â€ â€™v{entry.VersionAfter}");

        // The same node (test-doc-nodechange) was Created by parent and Patched by sub-thread.
        // Aggregation should give: min(VersionBefore), max(VersionAfter)
        var docChanges = responseMsg.UpdatedNodes.Where(e => e.Path.Contains("test-doc-nodechange")).ToList();
        docChanges.Should().ContainSingle(
            "changes to same node should be aggregated into one entry");
        var docChange = docChanges[0];
        (docChange.VersionAfter ?? 0).Should().BeGreaterThan(docChange.VersionBefore ?? 0,
            "aggregated version should show progression from create to patch");
        Output.WriteLine($"Aggregated: {docChange.Path} {docChange.Operation} v{docChange.VersionBefore}Ã¢â€ â€™v{docChange.VersionAfter}");
        }
        finally
        {
            SharedOrleansFixture.SwappableFactory.Reset();
        }
    }

    // Resubmit_AfterExecution_DoesNotDeadlock was split out into its own class
    // (OrleansResubmitDeadlockTest) so it no longer runs back-to-back with the
    // heavy delegation chain above -- see that file's class summary for why.
}

/// <summary>
/// Chat client that exercises the full tool-calling and delegation chain:
/// - Turn 1: calls Create tool (creates a Markdown node)
/// - Turn 2 (after Create result): calls delegate_to_agent (Executor)
/// - Turn 3 (after delegation result): returns summary text
/// </summary>
internal class ToolCallDelegatingChatClient : IChatClient
{
    private int _callCount;
    public ChatClientMetadata Metadata => new("ToolCallDelegating");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        var hasFunctionResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        var hasCreateResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>()
            .Any(f => f.CallId == "call_create"));
        var hasDelegateResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>()
            .Any(f => f.CallId == "call_delegate"));

        // After delegation completes: return summary text
        if (hasDelegateResult)
        {
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    "Created the document and delegated updates. All node changes should propagate.")));
        }

        // After Create tool returns: call delegate_to_agent
        if (hasCreateResult && options?.Tools?.Any(t => t.Name == "delegate_to_agent") == true)
        {
            var delegateCall = new FunctionCallContent("call_delegate", "delegate_to_agent",
                new Dictionary<string, object?>
                {
                    ["agentName"] = "Worker",
                    ["task"] = "Patch the node at TestUser/test-doc-nodechange: set name to 'Updated by sub-agent'"
                });
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [delegateCall])));
        }

        // First call: Create a Markdown node via MeshPlugin
        if (options?.Tools?.Any(t => t.Name == "Create") == true)
        {
            var nodeJson = JsonSerializer.Serialize(new
            {
                id = "test-doc-nodechange",
                @namespace = "TestUser",
                nodeType = "Markdown",
                name = "Test Doc for NodeChange",
                content = "# Initial Content"
            });
            var createCall = new FunctionCallContent("call_create", "Create",
                new Dictionary<string, object?> { ["node"] = nodeJson });
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [createCall])));
        }

        // Fallback
        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Done.")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Delegate to non-streaming for simplicity Ã¢â‚¬â€ the framework handles both
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var msg = response.Messages.First();
        var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
        if (functionCalls.Count > 0)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [..functionCalls]
            };
            yield break;
        }
        // Stream text word by word
        foreach (var word in (msg.Text ?? "Done.").Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            await Task.Delay(10, cancellationToken);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
}

/// <summary>
/// Sub-agent chat client (Worker/Executor) that calls Patch tool:
/// - Turn 1: calls Patch on the node created by the parent
/// - Turn 2 (after Patch result): returns text
/// </summary>
internal class PatchToolChatClient : IChatClient
{
    private int _callCount;
    public ChatClientMetadata Metadata => new("PatchTool");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _callCount);
        var hasFunctionResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());

        if (hasFunctionResult)
        {
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    "Patched the document successfully. Node changes tracked.")));
        }

        // First call: Patch the node
        if (options?.Tools?.Any(t => t.Name == "Patch") == true)
        {
            var fieldsJson = JsonSerializer.Serialize(new { name = "Updated by sub-agent" });
            var patchCall = new FunctionCallContent("call_patch", "Patch",
                new Dictionary<string, object?>
                {
                    ["path"] = "TestUser/test-doc-nodechange",
                    ["fields"] = fieldsJson
                });
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [patchCall])));
        }

        return Task.FromResult(new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "No Patch tool available.")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        var msg = response.Messages.First();
        var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
        if (functionCalls.Count > 0)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [..functionCalls]
            };
            yield break;
        }
        foreach (var word in (msg.Text ?? "Done.").Split(' '))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            await Task.Delay(10, cancellationToken);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(IChatClient) ? this : null;
    public void Dispose() { }
}

/// <summary>
/// Factory: top-level agents (Navigator/Orchestrator/IsDefault) get
/// <see cref="ToolCallDelegatingChatClient"/>; sub-agents (Worker/Executor/Coder)
/// get <see cref="PatchToolChatClient"/>. Extends <see cref="ChatClientAgentFactory"/>
/// so MeshPlugin's Create/Patch/delegate_to_agent tools are wired into every agent â€”
/// otherwise the fake chat clients stream <c>FunctionCallContent</c> for tools the
/// agent doesn't have and <c>FunctionInvokingChatClient</c> never executes them
/// (responseMsg.ToolCalls would stay empty).
/// </summary>
internal class NodeChangeTestChatClientFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
{
    public override string Name => "NodeChangeTestFactory";
    public override IReadOnlyList<string> Models => ["fake-model"];
    public override int Order => 0;

    protected override IChatClient CreateChatClient(AgentConfiguration agentConfig)
    {
        var isTopLevel = agentConfig.IsDefault || agentConfig.Id is "Navigator" or "Orchestrator";
        return isTopLevel
            ? new ToolCallDelegatingChatClient()
            : new PatchToolChatClient();
    }
}

/// <summary>
/// Silo configurator: production-like setup with Graph + AI + RLS + NodeChangeTestChatClientFactory.
/// </summary>
public class NodeChangePropagationSiloConfigurator : ISiloConfigurator, IHostConfigurator
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
            .AddMeshNodes(
                new MeshNode("TestUser", "User") { Name = "TestUser", NodeType = "User" })
            .AddMeshNodes(PublicEditorAccess())
            .ConfigureServices(services =>
                services.AddSingleton<IChatClientFactory>(sp =>
                    new NodeChangeTestChatClientFactory(sp.GetRequiredService<IMessageHub>())))
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

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
            // Namespace must end in "/_Access" â€” see SecurityService.ComputeScopeRoles.
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

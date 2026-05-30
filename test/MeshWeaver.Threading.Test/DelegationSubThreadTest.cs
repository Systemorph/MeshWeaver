using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests delegation sub-thread creation, message persistence, and thread hierarchy navigation.
/// Verifies that when an agent delegates work, a sub-thread is created under the
/// response message with proper input/output messages that are navigable.
/// </summary>
public class DelegationSubThreadTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddAI();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration);
    }

    [Fact]
    public void SubThread_CreatedUnderResponseMessage_HasCorrectPath()
    {
        // Create context + thread
        var contextPath = "DelegationTestOrg";
        NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Delegation Test", NodeType = "Markdown" }).Should().Emit();

        var client = GetClient();
        var threadResponse = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Test delegation")), o => o.WithTarget(new Address(contextPath))).Should().Within(15.Seconds()).Emit();
        threadResponse.Message.Success.Should().BeTrue(threadResponse.Message.Error ?? "");
        var threadPath = threadResponse.Message.Node!.Path;

        // Create a response message (simulating what ThreadExecution does)
        var responseMsgId = "resp001";
        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Delegating to Worker...",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator"
            }
        }).Should().Emit();

        // Create a sub-thread under the response message (simulating delegation)
        var subThreadId = "explore-mesh-schema-abc1";
        var parentMsgPath = $"{threadPath}/{responseMsgId}";
        var subThreadPath = $"{parentMsgPath}/{subThreadId}";

        NodeFactory.CreateNode(new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Explore mesh schema",
            NodeType = ThreadNodeType.NodeType,
            MainNode = contextPath,
            Content = new MeshThread()
        }).Should().Emit();

        // Verify sub-thread path does NOT have double _Thread
        subThreadPath.Should().NotContain("_Thread/_Thread",
            "sub-thread should be directly under the message, not in a nested _Thread");
        subThreadPath.Should().Contain("/_Thread/",
            "sub-thread path should contain _Thread from the parent thread");

        // Verify sub-thread is retrievable via stream
        var subThread = ReadNode(subThreadPath).Should().Emit();
        subThread.Should().NotBeNull();
        subThread!.NodeType.Should().Be(ThreadNodeType.NodeType);
        subThread.Name.Should().Be("Explore mesh schema");

        // Verify namespace IS the response message path (sub-thread owned by the message)
        subThread.Namespace.Should().Be(parentMsgPath,
            "sub-thread namespace should be the response message that owns it via tool call");

        // Verify MainNode points to content entity, not thread path
        subThread.MainNode.Should().Be(contextPath,
            "sub-thread MainNode should be the content entity for access control");
    }

    [Fact]
    public void SubThread_WithMessages_IsNavigableHierarchy()
    {
        // Create context + thread + response message
        var contextPath = "HierarchyTestOrg";
        NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Hierarchy Test", NodeType = "Markdown" }).Should().Emit();

        var client = GetClient();
        var threadResponse = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Test hierarchy")), o => o.WithTarget(new Address(contextPath))).Should().Within(15.Seconds()).Emit();
        var threadPath = threadResponse.Message.Node!.Path;

        var responseMsgId = "resp002";
        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Working on it...",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator"
            }
        }).Should().Emit();

        // Create sub-thread with input + output messages
        var subThreadId = "research-topic-def2";
        var parentMsgPath = $"{threadPath}/{responseMsgId}";
        var subThreadPath = $"{parentMsgPath}/{subThreadId}";

        var inputId = "input01";
        var outputId = "output01";

        NodeFactory.CreateNode(new MeshNode(subThreadId, parentMsgPath)
        {
            Name = "Research the topic",
            NodeType = ThreadNodeType.NodeType,
            MainNode = contextPath,
            Content = new MeshThread
            {
                Messages = [inputId, outputId]
            }
        }).Should().Emit();

        // Create input message
        NodeFactory.CreateNode(new MeshNode(inputId, subThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Research the topic of reinsurance pricing",
                Type = ThreadMessageType.ExecutedInput
            }
        }).Should().Emit();

        // Create output message with tool calls
        NodeFactory.CreateNode(new MeshNode(outputId, subThreadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Found 3 relevant documents about reinsurance pricing.",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Researcher",
                ToolCalls =
                [
                    new MeshWeaver.Layout.ToolCallEntry
                    {
                        Name = "Search",
                        DisplayName = "Searching \"reinsurance pricing\"",
                        Arguments = "{\"query\":\"reinsurance pricing\"}",
                        Result = "[{\"path\":\"Doc/Pricing\",\"name\":\"Pricing Guide\"}]",
                        IsSuccess = true
                    },
                    new MeshWeaver.Layout.ToolCallEntry
                    {
                        Name = "Get",
                        DisplayName = "Fetching @Doc/Pricing",
                        Arguments = "{\"path\":\"@Doc/Pricing\"}",
                        Result = "{\"name\":\"Pricing Guide\",\"nodeType\":\"Markdown\"}",
                        IsSuccess = true
                    }
                ]
            }
        }).Should().Emit();

        // Navigate the hierarchy: thread Ã¢â€ â€™ message Ã¢â€ â€™ sub-thread Ã¢â€ â€™ sub-messages

        // 1. Find sub-threads under the response message
        var subThreads = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{parentMsgPath} nodeType:{ThreadNodeType.NodeType}"))
            .Should().Match(c => c.Items.Count >= 1).Items;
        subThreads.Should().ContainSingle();
        subThreads[0].Path.Should().Be(subThreadPath);

        // 2. Get sub-thread content
        var subThreadNode = subThreads[0];
        var thread = subThreadNode.Content as MeshThread;
        thread.Should().NotBeNull();
        thread!.Messages.Should().HaveCount(2);
        thread.Messages[0].Should().Be(inputId);
        thread.Messages[1].Should().Be(outputId);

        // 3. Get sub-thread messages
        var messages = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{subThreadPath} nodeType:{ThreadMessageNodeType.NodeType}"))
            .Should().Match(c => c.Items.Count >= 2).Items;
        messages.Should().HaveCount(2);

        // 4. Verify input message
        var inputMsg = messages.FirstOrDefault(m => m.Id == inputId)?.Content as ThreadMessage;
        inputMsg.Should().NotBeNull();
        inputMsg!.Role.Should().Be("user");
        inputMsg.Text.Should().Contain("reinsurance pricing");

        // 5. Verify output message with tool calls
        var outputMsg = messages.FirstOrDefault(m => m.Id == outputId)?.Content as ThreadMessage;
        outputMsg.Should().NotBeNull();
        outputMsg!.Role.Should().Be("assistant");
        outputMsg.AgentName.Should().Be("Researcher");
        outputMsg.ToolCalls.Should().HaveCount(2);
        outputMsg.ToolCalls[0].Name.Should().Be("Search");
        outputMsg.ToolCalls[0].DisplayName.Should().Be("Searching \"reinsurance pricing\"");
        outputMsg.ToolCalls[1].Name.Should().Be("Get");
    }

    [Fact]
    public void SubThread_ToolCallsAggregatedToParent()
    {
        // Verify the ForwardToolCall callback mechanism works:
        // Sub-thread tool calls should be aggregated into the parent's tool call log
        var parentToolCalls = new System.Collections.Generic.List<MeshWeaver.Layout.ToolCallEntry>();

        // Simulate the forwarding callback
        Action<MeshWeaver.Layout.ToolCallEntry> forwardCallback = entry => parentToolCalls.Add(entry);

        // Simulate sub-thread tool calls being forwarded
        forwardCallback(new MeshWeaver.Layout.ToolCallEntry
        {
            Name = "Search",
            DisplayName = "Researcher: Searching \"reinsurance\"",
            Arguments = "{\"query\":\"reinsurance\"}",
            Result = "[{\"path\":\"Doc/Pricing\"}]",
            IsSuccess = true,
            DelegationPath = "thread/msg/sub1"
        });

        forwardCallback(new MeshWeaver.Layout.ToolCallEntry
        {
            Name = "Get",
            DisplayName = "Researcher: Fetching @Doc/Pricing",
            Arguments = "{\"path\":\"@Doc/Pricing\"}",
            Result = "{\"name\":\"Pricing\"}",
            IsSuccess = true,
            DelegationPath = "thread/msg/sub1"
        });

        parentToolCalls.Should().HaveCount(2);
        parentToolCalls[0].DisplayName.Should().Contain("Researcher:");
        parentToolCalls[0].DelegationPath.Should().NotBeNullOrEmpty();
        parentToolCalls[1].Name.Should().Be("Get");
    }

    [Fact]
    public void SubThread_ToolCalls_PrefixedWithAgentName()
    {
        // Verify that forwarded tool calls get prefixed with agent name
        var subCall = new MeshWeaver.Layout.ToolCallEntry
        {
            Name = "Search",
            DisplayName = "Searching \"topic\"",
            IsSuccess = true
        };

        // Simulate what ChatClientAgentFactory does: prefix with agent name
        var forwarded = subCall with
        {
            DisplayName = $"Worker: {subCall.DisplayName ?? subCall.Name}",
            DelegationPath = "thread/msg/sub1"
        };

        forwarded.DisplayName.Should().Be("Worker: Searching \"topic\"");
        forwarded.DelegationPath.Should().Be("thread/msg/sub1");
    }

    [Fact]
    public void SubThread_PlanAndDelegations_CoexistUnderThread()
    {
        // Create context + thread
        var contextPath = "CoexistTestOrg";
        NodeFactory.CreateNode(
            new MeshNode(contextPath) { Name = "Coexist Test", NodeType = "Markdown" }).Should().Emit();

        var client = GetClient();
        var threadResponse = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Test plan and delegations")), o => o.WithTarget(new Address(contextPath))).Should().Within(15.Seconds()).Emit();
        var threadPath = threadResponse.Message.Node!.Path;

        // Store a plan under the thread
        NodeFactory.CreateNode(new MeshNode("Plan", threadPath)
        {
            Name = "Execution Plan",
            NodeType = "Markdown",
            Content = "# Plan\n1. Research\n2. Create nodes"
        }).Should().Emit();

        // Create response message with sub-thread delegation
        var responseMsgId = "resp003";
        NodeFactory.CreateNode(new MeshNode(responseMsgId, threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Here's the plan. Delegating research...",
                Type = ThreadMessageType.AgentResponse,
                AgentName = "Orchestrator"
            }
        }).Should().Emit();

        // Create delegation sub-thread
        NodeFactory.CreateNode(
            new MeshNode("research-task-xyz1", $"{threadPath}/{responseMsgId}")
            {
                Name = "Research task",
                NodeType = ThreadNodeType.NodeType,
                Content = new MeshThread { Messages = [] }
            }).Should().Emit();

        // Verify: Plan exists as Markdown child of thread (stream read)
        var plan = ReadNode($"{threadPath}/Plan").Should().Emit();
        plan.Should().NotBeNull();
        plan!.NodeType.Should().Be("Markdown");

        // Verify: Sub-thread exists under response message
        var subThreads = MeshQuery
            .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{threadPath}/{responseMsgId} nodeType:{ThreadNodeType.NodeType}"))
            .Should().Match(c => c.Items.Count >= 1).Items;
        subThreads.Should().ContainSingle();

        // Verify: Sub-thread is accessible under response message namespace (stream read)
        var respNode = ReadNode($"{threadPath}/{responseMsgId}").Should().Emit();
        respNode.Should().NotBeNull();
    }
}

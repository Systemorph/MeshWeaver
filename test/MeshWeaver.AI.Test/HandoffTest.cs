#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for agent handoff: verifies that when agent A hands off to agent B,
/// the handoff request is set, the tool returns an immediate stop message,
/// and the HandoffTool is created correctly.
/// </summary>
public class HandoffTest
{
    #region Fake Chat Client Infrastructure

    /// <summary>
    /// Simple chat client that always returns a canned text response.
    /// </summary>
    private class SimpleFakeChatClient : IChatClient
    {
        private readonly string response;

        public SimpleFakeChatClient(string response) => this.response = response;

        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var msg = new ChatMessage(ChatRole.Assistant, response);
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in response.Split(' '))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    /// <summary>
    /// Chat client that triggers a handoff tool call on first invocation.
    /// Simulates an LLM that decides to hand off to another agent.
    /// </summary>
    private class HandoffFakeChatClient : IChatClient
    {
        private readonly string toolName;
        private readonly string targetAgentName;
        private readonly string handoffMessage;

        public HandoffFakeChatClient(
            string toolName,
            string targetAgentName,
            string handoffMessage)
        {
            this.toolName = toolName;
            this.targetAgentName = targetAgentName;
            this.handoffMessage = handoffMessage;
        }

        public ChatClientMetadata Metadata => new("FakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();

            // Check if any message contains a function result (tool response)
            var hasFunctionResult = messageList.Any(m =>
                m.Contents.Any(c => c is FunctionResultContent));

            if (!hasFunctionResult)
            {
                // First call: trigger handoff tool
                var functionCall = new FunctionCallContent(
                    callId: "handoff_call_1",
                    name: toolName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["agentName"] = targetAgentName,
                        ["message"] = handoffMessage
                    });
                var msg = new ChatMessage(ChatRole.Assistant, [functionCall]);
                return Task.FromResult(new ChatResponse(msg));
            }
            else
            {
                // After tool result: the handoff tool says to stop, so just acknowledge
                var msg = new ChatMessage(ChatRole.Assistant, "Handoff complete.");
                return Task.FromResult(new ChatResponse(msg));
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var msg in response.Messages)
            {
                if (!string.IsNullOrEmpty(msg.Text))
                    yield return new ChatResponseUpdate(ChatRole.Assistant, msg.Text);
            }
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    #endregion

    /// <summary>
    /// Verifies that the HandoffTool sets a HandoffRequest when invoked.
    /// </summary>
    [Fact]
    public async Task Handoff_ToolSetsHandoffRequest()
    {
        // Arrange
        HandoffRequest? capturedRequest = null;

        var agentAConfig = new AgentConfiguration
        {
            Id = "AgentA",
            Handoffs = [new AgentHandoff { AgentPath = "AgentB", Instructions = "Takes over for complex tasks" }]
        };

        var handoffTool = HandoffTool.CreateUnifiedHandoffTool(
            agentAConfig,
            [agentAConfig],
            requestHandoff: req => capturedRequest = req);

        // Create agent A with the handoff tool
        var agentAClient = new HandoffFakeChatClient(
            toolName: "handoff_to_agent",
            targetAgentName: "AgentB",
            handoffMessage: "Take over this complex planning task");

        var agentA = new ChatClientAgent(
            chatClient: agentAClient,
            instructions: "You are Agent A.",
            name: "AgentA",
            description: "Coordinator",
            tools: [handoffTool],
            loggerFactory: null,
            services: null
        );

        // Act
        var session = await agentA.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        await agentA.RunAsync("Complex task requiring planning", session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: HandoffRequest was captured
        capturedRequest.Should().NotBeNull("the handoff tool should set a HandoffRequest");
        capturedRequest!.SourceAgentName.Should().Be("AgentA");
        capturedRequest.TargetAgentName.Should().Be("AgentB");
        capturedRequest.Message.Should().Be("Take over this complex planning task");
    }

    /// <summary>
    /// Verifies that the handoff tool returns a stop message,
    /// so the LLM knows to stop generating.
    /// </summary>
    [Fact]
    public async Task Handoff_ToolReturnsStopMessage()
    {
        // Arrange
        var agentAConfig = new AgentConfiguration
        {
            Id = "AgentA",
            Handoffs = [new AgentHandoff { AgentPath = "AgentB", Instructions = "Takes over" }]
        };

        var handoffTool = HandoffTool.CreateUnifiedHandoffTool(
            agentAConfig,
            [agentAConfig],
            requestHandoff: _ => { });

        var agentAClient = new HandoffFakeChatClient(
            toolName: "handoff_to_agent",
            targetAgentName: "AgentB",
            handoffMessage: "Handle this");

        var agentA = new ChatClientAgent(
            chatClient: agentAClient,
            instructions: "You are Agent A.",
            name: "AgentA",
            description: "Coordinator",
            tools: [handoffTool],
            loggerFactory: null,
            services: null
        );

        // Act
        var session = await agentA.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await agentA.RunAsync("Do something", session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: The function result contains the stop message
        var functionResults = result.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        functionResults.Should().NotBeEmpty("handoff tool should produce a function result");

        var handoffResult = functionResults
            .Select(fr => fr.Result?.ToString() ?? "")
            .FirstOrDefault(r => r.Contains("Handoff initiated"));

        handoffResult.Should().NotBeNull(
            "the handoff tool result should tell the LLM to stop");
    }

    /// <summary>
    /// Verifies that HandoffRequest record has correct values.
    /// </summary>
    [Fact]
    public void HandoffRequest_RecordProperties_AreCorrect()
    {
        var request = new HandoffRequest("Orchestrator", "Planner", "Plan this complex task");

        request.SourceAgentName.Should().Be("Orchestrator");
        request.TargetAgentName.Should().Be("Planner");
        request.Message.Should().Be("Plan this complex task");
    }

    /// <summary>
    /// Verifies that ChatHandoffContent properties are set correctly.
    /// </summary>
    [Fact]
    public void ChatHandoffContent_Properties_AreCorrect()
    {
        var content = new ChatHandoffContent("Orchestrator", "Planner", "Take over planning");

        content.SourceAgent.Should().Be("Orchestrator");
        content.TargetAgent.Should().Be("Planner");
        content.HandoffMessage.Should().Be("Take over planning");
    }

    /// <summary>
    /// Verifies that the HandoffTool includes available agents in its description.
    /// </summary>
    [Fact]
    public void HandoffTool_Description_IncludesAvailableAgents()
    {
        var agentConfig = new AgentConfiguration
        {
            Id = "AgentA",
            Handoffs =
            [
                new AgentHandoff { AgentPath = "Agent/Planner", Instructions = "Complex planning" },
                new AgentHandoff { AgentPath = "Agent/Worker", Instructions = "Task execution" }
            ]
        };

        var tool = HandoffTool.CreateUnifiedHandoffTool(
            agentConfig,
            [agentConfig],
            requestHandoff: _ => { });

        // The tool should be an AIFunction with description containing agent info
        var aiFunction = tool as AIFunction;
        aiFunction.Should().NotBeNull();
        aiFunction!.Name.Should().Be("handoff_to_agent");
        aiFunction.Description.Should().Contain("Agent/Planner");
        aiFunction.Description.Should().Contain("Agent/Worker");
        aiFunction.Description.Should().Contain("Complex planning");
        aiFunction.Description.Should().Contain("Task execution");
    }

    /// <summary>
    /// Verifies that AgentConfiguration supports both delegations and handoffs simultaneously.
    /// </summary>
    [Fact]
    public void AgentConfiguration_SupportsCoexistingDelegationsAndHandoffs()
    {
        var config = new AgentConfiguration
        {
            Id = "Orchestrator",
            Delegations =
            [
                new AgentDelegation { AgentPath = "Agent/Researcher", Instructions = "Lookup info" }
            ],
            Handoffs =
            [
                new AgentHandoff { AgentPath = "Agent/Planner", Instructions = "Take over planning" }
            ]
        };

        config.Delegations.Should().HaveCount(1);
        config.Delegations![0].AgentPath.Should().Be("Agent/Researcher");

        config.Handoffs.Should().HaveCount(1);
        config.Handoffs![0].AgentPath.Should().Be("Agent/Planner");
    }
}

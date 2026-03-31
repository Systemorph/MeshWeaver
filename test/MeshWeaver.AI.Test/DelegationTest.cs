#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
/// Tests for agent delegation: verifies that when agent A delegates to agent B,
/// both sessions (threads) are created, the child session is isolated,
/// and the delegation result flows back to the parent.
/// </summary>
public class DelegationTest
{
    private const string AgentBResponseText = "This is the specialized response from Agent B.";

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
    /// Chat client that triggers a tool call on first invocation,
    /// then returns a text summary after receiving the tool result.
    /// Simulates an LLM that decides to delegate to another agent.
    /// </summary>
    private class DelegatingFakeChatClient : IChatClient
    {
        private readonly string toolName;
        private readonly string targetAgentName;
        private readonly string delegationTask;
        private readonly string summaryPrefix;

        public DelegatingFakeChatClient(
            string toolName,
            string targetAgentName,
            string delegationTask,
            string summaryPrefix = "Summary from parent:")
        {
            this.toolName = toolName;
            this.targetAgentName = targetAgentName;
            this.delegationTask = delegationTask;
            this.summaryPrefix = summaryPrefix;
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
                // First call: trigger delegation tool
                var functionCall = new FunctionCallContent(
                    callId: "delegation_call_1",
                    name: toolName,
                    arguments: new Dictionary<string, object?>
                    {
                        ["agentName"] = targetAgentName,
                        ["task"] = delegationTask
                    });
                var msg = new ChatMessage(ChatRole.Assistant, [functionCall]);
                return Task.FromResult(new ChatResponse(msg));
            }
            else
            {
                // After tool result: extract the delegation result and include it in summary
                var toolResult = messageList
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .FirstOrDefault();
                var resultText = toolResult?.Result?.ToString() ?? "";
                var msg = new ChatMessage(ChatRole.Assistant, $"{summaryPrefix} {resultText}");
                return Task.FromResult(new ChatResponse(msg));
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // RunAsync uses GetResponseAsync (non-streaming). Minimal streaming implementation.
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
    /// Verifies that when agent A delegates to agent B:
    /// 1. Both agent sessions (threads) are created
    /// 2. Agent B's session is isolated from agent A's session
    /// 3. The delegation result from B flows back into A's response
    /// 4. B's session is created within A's execution scope (namespace)
    /// </summary>
    [Fact]
    public async Task Delegation_BothSessionsCreated_ChildRunsInIsolatedContext()
    {
        // Arrange: Create agent B (the target/child)
        var agentBClient = new SimpleFakeChatClient(AgentBResponseText);
        var agentB = new ChatClientAgent(
            chatClient: agentBClient,
            instructions: "You are Agent B, a specialized worker.",
            name: "AgentB",
            description: "Agent B for specialized tasks",
            tools: [],
            loggerFactory: null,
            services: null
        );

        // Track delegation execution
        AgentSession? childSession = null;
        string? capturedAgentName = null;
        string? capturedTask = null;

        // Create agent configurations
        var agentAConfig = new AgentConfiguration
        {
            Id = "AgentA",
            Delegations = [new AgentDelegation { AgentPath = "AgentB", Instructions = "Handles specialized tasks" }]
        };
        var agentBConfig = new AgentConfiguration
        {
            Id = "AgentB",
            Description = "Agent B for specialized tasks"
        };

        var allAgents = new Dictionary<string, ChatClientAgent> { ["AgentB"] = agentB };

        // Create delegation tool with the same executeAsync pattern as ChatClientAgentFactory
        var delegationTool = DelegationTool.CreateUnifiedDelegationTool(
            agentAConfig,
            [agentAConfig, agentBConfig],
            executeAsync: async (agentName, task, context, ct) =>
            {
                capturedAgentName = agentName;
                capturedTask = task;

                var targetId = agentName.Split('/').Last();
                if (!allAgents.TryGetValue(targetId, out var targetAgent))
                {
                    return new DelegationResult
                    {
                        AgentName = agentName,
                        Task = task,
                        Result = $"Agent '{agentName}' not found",
                        Success = false
                    };
                }

                // Create isolated session for the child agent (same as production code)
                childSession = await targetAgent.CreateSessionAsync();

                // Run the child agent
                var response = await targetAgent.RunAsync(task, childSession, cancellationToken: ct);

                // Extract text from response
                var resultText = string.Join("\n", response.Messages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .SelectMany(m => m.Contents)
                    .OfType<TextContent>()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrEmpty(t)));

                return new DelegationResult
                {
                    AgentName = targetId,
                    Task = task,
                    Result = resultText,
                    Success = true
                };
            });

        // Create agent A (the delegator) with a chat client that triggers the delegation tool
        var agentAClient = new DelegatingFakeChatClient(
            toolName: "delegate_to_agent",
            targetAgentName: "AgentB",
            delegationTask: "Process this specialized request");

        var agentA = new ChatClientAgent(
            chatClient: agentAClient,
            instructions: "You are Agent A, a coordinator.",
            name: "AgentA",
            description: "Agent A coordinates work",
            tools: [delegationTool],
            loggerFactory: null,
            services: null
        );

        // Act: Run agent A (which triggers delegation to B)
        var parentSession = await agentA.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await agentA.RunAsync("User query requiring delegation", parentSession, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: Both sessions were created
        parentSession.Should().NotBeNull("parent agent A should have a session (thread)");
        childSession.Should().NotBeNull("child agent B should have a session (thread) created during delegation");

        // Assert: Sessions are isolated (different instances)
        childSession.Should().NotBeSameAs(parentSession,
            "child session should be isolated from parent — B runs in its own namespace within A's execution");

        // Assert: Delegation was executed with correct parameters
        capturedAgentName.Should().Be("AgentB");
        capturedTask.Should().Be("Process this specialized request");

        // Assert: Agent A's response includes text that references B's result
        var responseTexts = result.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        responseTexts.Should().NotBeEmpty("parent agent A should produce a response after delegation");

        // The parent's final text should contain B's response (relayed via tool result → summary)
        var fullResponse = string.Join(" ", responseTexts);
        fullResponse.Should().Contain(AgentBResponseText,
            "A's response should include B's delegation result");
    }

    /// <summary>
    /// Verifies that B's thread is in the namespace of A's thread:
    /// The delegation tool result (containing B's response) appears as a function result
    /// in A's message history, proving B's execution is scoped within A's thread.
    /// </summary>
    [Fact]
    public async Task Delegation_ChildThreadInParentNamespace_ResultAppearsAsFunctionResultInParent()
    {
        // Arrange: Create agents
        var agentBClient = new SimpleFakeChatClient(AgentBResponseText);
        var agentB = new ChatClientAgent(
            chatClient: agentBClient,
            instructions: "You are Agent B.",
            name: "AgentB",
            description: "Specialized agent",
            tools: [],
            loggerFactory: null,
            services: null
        );

        var agentAConfig = new AgentConfiguration
        {
            Id = "AgentA",
            Delegations = [new AgentDelegation { AgentPath = "AgentB", Instructions = "Specialist" }]
        };
        var agentBConfig = new AgentConfiguration { Id = "AgentB", Description = "Specialist" };

        var allAgents = new Dictionary<string, ChatClientAgent> { ["AgentB"] = agentB };

        var delegationTool = DelegationTool.CreateUnifiedDelegationTool(
            agentAConfig,
            [agentAConfig, agentBConfig],
            executeAsync: async (agentName, task, context, ct) =>
            {
                var targetId = agentName.Split('/').Last();
                var targetAgent = allAgents[targetId];
                var session = await targetAgent.CreateSessionAsync();
                var response = await targetAgent.RunAsync(task, session, cancellationToken: ct);

                var resultText = string.Join("\n", response.Messages
                    .Where(m => m.Role == ChatRole.Assistant)
                    .SelectMany(m => m.Contents)
                    .OfType<TextContent>()
                    .Select(t => t.Text)
                    .Where(t => !string.IsNullOrEmpty(t)));

                return new DelegationResult
                {
                    AgentName = targetId,
                    Task = task,
                    Result = resultText,
                    Success = true
                };
            });

        var agentAClient = new DelegatingFakeChatClient(
            toolName: "delegate_to_agent",
            targetAgentName: "AgentB",
            delegationTask: "Handle this");

        var agentA = new ChatClientAgent(
            chatClient: agentAClient,
            instructions: "You are Agent A.",
            name: "AgentA",
            description: "Coordinator",
            tools: [delegationTool],
            loggerFactory: null,
            services: null
        );

        // Act
        var parentSession = await agentA.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await agentA.RunAsync("Delegate this work", parentSession, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: The delegation tool call appears in A's message history
        var functionCalls = result.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        functionCalls.Should().Contain(fc => fc.Name == "delegate_to_agent",
            "A's thread should contain the delegation tool call");

        // Assert: The delegation result appears as a FunctionResultContent in A's thread
        var functionResults = result.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        functionResults.Should().NotBeEmpty(
            "B's delegation result should appear as a function result in A's thread (B is in A's namespace)");

        // Assert: The function result contains B's actual response text
        var delegationResultText = functionResults
            .Select(fr => fr.Result?.ToString() ?? "")
            .FirstOrDefault(r => r.Contains("Agent B"));

        delegationResultText.Should().NotBeNull(
            "the function result in A's thread should contain B's response");
    }

    /// <summary>
    /// Verifies that delegation to a non-existent agent returns a failure result
    /// rather than throwing an exception, and the parent agent can still respond.
    /// </summary>
    [Fact]
    public async Task Delegation_TargetAgentNotFound_ReturnsFailureResult()
    {
        // Arrange: Create delegation tool with empty agents dictionary
        var agentAConfig = new AgentConfiguration
        {
            Id = "AgentA",
            Delegations = [new AgentDelegation { AgentPath = "NonExistent", Instructions = "Does not exist" }]
        };

        var allAgents = new Dictionary<string, ChatClientAgent>();
        bool delegationExecuted = false;

        var delegationTool = DelegationTool.CreateUnifiedDelegationTool(
            agentAConfig,
            [agentAConfig],
            executeAsync: async (agentName, task, context, ct) =>
            {
                delegationExecuted = true;
                var targetId = agentName.Split('/').Last();
                if (!allAgents.TryGetValue(targetId, out var targetAgent))
                {
                    return new DelegationResult
                    {
                        AgentName = agentName,
                        Task = task,
                        Result = $"Agent '{agentName}' not found",
                        Success = false
                    };
                }

                // Should not reach here
                await Task.CompletedTask;
                return new DelegationResult
                {
                    AgentName = targetId,
                    Task = task,
                    Result = "unexpected",
                    Success = true
                };
            });

        var agentAClient = new DelegatingFakeChatClient(
            toolName: "delegate_to_agent",
            targetAgentName: "NonExistent",
            delegationTask: "This should fail gracefully");

        var agentA = new ChatClientAgent(
            chatClient: agentAClient,
            instructions: "You are Agent A.",
            name: "AgentA",
            description: "Coordinator",
            tools: [delegationTool],
            loggerFactory: null,
            services: null
        );

        // Act
        var session = await agentA.CreateSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await agentA.RunAsync("Try to delegate", session, cancellationToken: TestContext.Current.CancellationToken);

        // Assert: Delegation was attempted
        delegationExecuted.Should().BeTrue("the delegation callback should have been invoked");

        // Assert: Agent A still produces a response (handles the failure gracefully)
        var responseTexts = result.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents)
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        responseTexts.Should().NotBeEmpty("parent agent should still respond after failed delegation");

        // Assert: The failure message was returned as a tool result
        var functionResults = result.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .ToList();

        functionResults.Should().Contain(fr =>
            fr.Result != null && fr.Result.ToString()!.Contains("not found"),
            "the failure result should indicate the agent was not found");
    }
}

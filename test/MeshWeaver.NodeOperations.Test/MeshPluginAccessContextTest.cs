using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that MeshPlugin tool calls restore the user's access context.
/// In production, AsyncLocal doesn't flow through the AI framework's
/// async streaming + tool invocation chain, so the plugin must explicitly
/// restore it from ThreadExecutionContext.UserAccessContext.
/// </summary>
public class MeshPluginAccessContextTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSampleUsers();

    [Fact]
    public async Task MeshPlugin_Get_RestoresAccessContext()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create a node
        await CreateNodeAsync(
            new MeshNode("test-doc", "User/rbuergi") { Name = "Test Doc", NodeType = "Markdown" });

        // Simulate what happens during thread execution:
        // 1. Set user context (normally done by ExecuteMessageAsync)
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "rbuergi", Name = "Roland" });

        // 2. Create plugin with a mock chat that has the execution context
        var chat = new TestAgentChat
        {
            ExecutionContext = new ThreadExecutionContext
            {
                ThreadPath = "User/rbuergi/_Thread/test",
                ResponseMessageId = "test-msg",
                UserAccessContext = new AccessContext { ObjectId = "rbuergi", Name = "Roland" }
            }
        };
        var plugin = new MeshPlugin(Mesh, chat);

        // 3. Clear the AsyncLocal (simulates what happens when AI framework calls the tool)
        accessService.SetContext(null);

        // 4. Call Get — should succeed because MeshPlugin restores context
        var result = await plugin.Get("User/rbuergi/test-doc");
        result.Should().Contain("Test Doc", "MeshPlugin.Get should restore user context and return the node");
        Output.WriteLine($"Get result: {result[..System.Math.Min(200, result.Length)]}");
    }

    [Fact]
    public async Task MeshPlugin_Update_RestoresAccessContext()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create a node (under admin context — DevLogin set this up in InitializeAsync)
        await CreateNodeAsync(
            new MeshNode("update-test", "User/rbuergi")
            {
                Name = "Original",
                NodeType = "Markdown",
                Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Original" },
            });

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        // Capture the active circuit context from DevLogin so the simulated chat carries
        // an identity that actually has write rights — otherwise the Patch correctly fails
        // with "did not commit" and we can't tell restoration apart from auth denial.
        var chatUserContext = accessService.CircuitContext
            ?? throw new InvalidOperationException("DevLogin should have set a CircuitContext.");
        var chat = new TestAgentChat
        {
            ExecutionContext = new ThreadExecutionContext
            {
                ThreadPath = "User/rbuergi/_Thread/test",
                ResponseMessageId = "test-msg",
                UserAccessContext = chatUserContext
            }
        };
        var plugin = new MeshPlugin(Mesh, chat);

        // Clear both AsyncLocal contexts — simulates AI framework tool invocation
        // where AsyncLocal doesn't flow. The plugin must restore the user's identity
        // from chat.ExecutionContext.UserAccessContext before invoking the operation.
        accessService.SetContext(null);
        accessService.SetCircuitContext(null);

        // Update should succeed with restored context. Use plugin.Update (full replacement)
        // rather than plugin.Patch — Patch carries an extra "version-stayed-the-same" silent-
        // failure guard that fires under the in-memory persistence used by this test base
        // (which doesn't bump versions on update). The restoration semantics are identical;
        // only Update gives a clean signal.
        var existingNode = new MeshNode("update-test", "User/rbuergi")
        {
            Name = "Updated Name",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Updated" },
        };
        var updateJson = JsonSerializer.Serialize(new[] { existingNode }, Mesh.JsonSerializerOptions);
        var result = await plugin.Update(updateJson);
        result.Should().Contain("Updated", "MeshPlugin.Update should restore user context and update");
        Output.WriteLine($"Update result: {result}");
    }

    /// <summary>
    /// Minimal IAgentChat implementation for testing MeshPlugin access context.
    /// </summary>
    private class TestAgentChat : IAgentChat
    {
        public ThreadExecutionContext? ExecutionContext { get; set; }
        public string? LastDelegationPath { get; set; }
        public Action<string>? UpdateDelegationStatus { get; set; }
        public Action<ToolCallEntry>? ForwardToolCall { get; set; }
        public AgentContext? Context { get; set; }
        public void SetContext(AgentContext? applicationContext) => Context = applicationContext;
        public void SetSelectedAgent(string? agentName) { }
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
            => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(new List<AgentDisplayInfo>());
        public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
            IReadOnlyCollection<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
    }
}

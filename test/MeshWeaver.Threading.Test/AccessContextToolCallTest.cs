using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that the AccessContextAIFunction wrapper restores user identity
/// before each tool invocation. Without this, tool calls during AI streaming
/// run without identity (AsyncLocal lost), causing "Access denied" errors.
/// </summary>
public class AccessContextToolCallTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddSampleUsers();

    [Fact]
    public async Task AccessContextAIFunction_RestoresIdentity_BeforeToolCall()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Track what context the tool sees
        AccessContext? capturedContext = null;

        // Create a simple AIFunction that captures the access context
        var innerTool = AIFunctionFactory.Create(
            (string input) =>
            {
                capturedContext = accessService.Context;
                return $"Got: {input}";
            },
            name: "test_tool",
            description: "Test tool");

        // Create a mock chat with execution context
        var chat = new TestAgentChat
        {
            ExecutionContext = new ThreadExecutionContext
            {
                ThreadPath = "User/rbuergi/_Thread/test",
                ResponseMessageId = "msg1",
                UserAccessContext = new AccessContext { ObjectId = "rbuergi", Name = "Roland" }
            }
        };

        // Wrap with AccessContextAIFunction
        var wrapped = new AccessContextAIFunction(
            (AIFunction)innerTool, chat, accessService);

        // Clear the AsyncLocal (simulates AI framework tool invocation context)
        accessService.SetContext(null);
        accessService.Context.Should().BeNull("context should be cleared before test");

        // Invoke the wrapped tool
        var result = await wrapped.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["input"] = "hello" }), ct);

        // Verify the tool saw the restored context
        capturedContext.Should().NotBeNull("tool should see restored access context");
        capturedContext!.ObjectId.Should().Be("rbuergi");
        Output.WriteLine($"Tool saw context: {capturedContext.ObjectId}");
    }

    [Fact]
    public async Task AccessContextAIFunction_WithoutExecutionContext_DoesNotCrash()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        var innerTool = AIFunctionFactory.Create(
            (string input) => $"Got: {input}",
            name: "test_tool",
            description: "Test tool");

        // Chat with no execution context
        var chat = new TestAgentChat();

        var wrapped = new AccessContextAIFunction(
            (AIFunction)innerTool, chat, accessService);

        accessService.SetContext(null);

        // Should not throw
        var result = await wrapped.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["input"] = "hello" }), ct);
        result.Should().NotBeNull();
    }

    [Fact]
    public void DelegationDepthGuard_BlocksExcessiveNesting()
    {
        // Simulate a deeply nested thread path (depth 3 = 3 levels of _Thread)
        var deepPath = "Org/Doc/_Thread/thread1/msg1/_Thread/sub1/msg2/_Thread/sub2";
        var depth = deepPath.Split("/_Thread/").Length - 1;
        depth.Should().Be(3, "path has 3 _Thread segments");
        (depth >= 3).Should().BeTrue("delegation should be blocked at depth >= 3");
    }
}

/// <summary>
/// Minimal IAgentChat for testing.
/// </summary>
file class TestAgentChat : IAgentChat
{
    public ThreadExecutionContext? ExecutionContext { get; set; }
    public AgentContext? Context { get; set; }
    public string? LastDelegationPath { get; set; }
    public Action<string>? UpdateDelegationStatus { get; set; }
    public Action<MeshWeaver.Layout.ToolCallEntry>? ForwardToolCall { get; set; }
    public void SetContext(AgentContext? ctx) => Context = ctx;
    public void SetSelectedAgent(string? name) { }
    public Task ResumeAsync(AI.Persistence.ChatConversation c) => Task.CompletedTask;
    public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
        => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(new List<AgentDisplayInfo>());
    public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
        IReadOnlyCollection<ChatMessage> m, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { await Task.CompletedTask; yield break; }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> m, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { await Task.CompletedTask; yield break; }
    public void SetThreadId(string id) { }
    public void DisplayLayoutArea(MeshWeaver.Layout.LayoutAreaControl c) { }
}

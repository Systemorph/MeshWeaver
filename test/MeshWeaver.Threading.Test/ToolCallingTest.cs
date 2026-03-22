using System;
using System.Collections.Generic;
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
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests for agent tool calling within the thread execution pipeline.
/// Uses a ToolCallingFakeChatClient that simulates an LLM calling the Search tool,
/// verifying the full round-trip: LLM → tool call → tool execution → LLM → response.
/// </summary>
public class ToolCallingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(sp => new ToolCallingFakeChatClientFactory(sp));
                return services;
            })
            .AddAI()
            .AddSampleUsers();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, text);
        var created = await NodeFactory.CreateNodeAsync(threadNode, ct);
        return created.Path;
    }

    private IObservable<IReadOnlyList<string>> ObserveMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                return (IReadOnlyList<string>)(content?.Messages ?? []);
            });
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), path)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(Mesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(Mesh.JsonSerializerOptions);
        return null;
    }

    /// <summary>
    /// End-to-end test: LLM calls the Search tool, tool executes against real mesh,
    /// LLM receives result and produces a response that includes tool output.
    /// Verifies the full tool-calling pipeline through the thread execution flow.
    /// </summary>
    [Fact]
    public async Task SubmitMessage_WithToolCalling_ExecutesSearchAndReturnsResult()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();

        // 1. Create some test data so the Search tool has something to find
        await NodeFactory.CreateNodeAsync(new MeshNode("test-doc", ContextPath)
        {
            Name = "Test Document",
            NodeType = "Markdown",
            Content = "Hello from test document"
        }, ct);

        // 2. Create thread
        var threadPath = await CreateThreadAsync(client, "Tool calling test", ct);
        Output.WriteLine($"Thread created: {threadPath}");

        // 3. Subscribe to Messages
        var twoMessages = ObserveMessages(client, threadPath)
            .Where(ids => ids.Count >= 2).FirstAsync().ToTask(ct);

        // 4. Submit message — the ToolCallingFakeChatClient will:
        //    a) Return a Search tool call on first invocation
        //    b) The ChatClientAgent framework will execute the Search tool
        //    c) On second invocation (with tool result), return a text response
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Search for test documents",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // 5. Wait for message IDs
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2);
        Output.WriteLine($"Messages: [{string.Join(", ", msgIds)}]");

        // 6. Wait for response to complete (poll until stable)
        ThreadMessage? responseContent = null;
        var prevLen = 0;
        var stable = 0;
        for (var i = 0; i < 50; i++)
        {
            responseContent = await GetHubContentAsync<ThreadMessage>(
                client, $"{threadPath}/{msgIds[1]}", ct);
            var len = responseContent?.Text?.Length ?? 0;
            if (len > 0 && len == prevLen && ++stable >= 2) break;
            else stable = 0;
            prevLen = len;
            await Task.Delay(200, ct);
        }

        responseContent.Should().NotBeNull("response should exist");
        responseContent!.Role.Should().Be("assistant");
        responseContent.Text.Should().NotBeNullOrEmpty("agent should produce a response after tool calling");

        // 7. Verify the response contains evidence of tool execution
        // The ToolCallingFakeChatClient includes "TOOL_RESULT_RECEIVED" in the final response
        responseContent.Text.Should().Contain("TOOL_RESULT_RECEIVED",
            "response should include marker proving the tool was called and result received");

        Output.WriteLine($"Response: '{responseContent.Text}' ({responseContent.Text.Length} chars)");
    }

    #region Tool-Calling Fake Chat Client

    /// <summary>
    /// Simulates an LLM that calls the Search tool on the first turn,
    /// then produces a text response on the second turn (after receiving tool results).
    /// </summary>
    private class ToolCallingFakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("ToolCallingFakeProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            var hasFunctionResult = messageList.Any(m =>
                m.Contents.OfType<FunctionResultContent>().Any());

            if (hasFunctionResult)
            {
                // Second turn: tool results are available, return text response
                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant,
                        "TOOL_RESULT_RECEIVED: Based on the search results, I found the data you requested.")));
            }

            // First turn: call the Search tool
            var toolCall = new FunctionCallContent("call_001", "Search",
                new Dictionary<string, object?> { ["query"] = "nodeType:Markdown" });
            var msg = new ChatMessage(ChatRole.Assistant, [toolCall]);
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Delegate to non-streaming and convert
            var response = await GetResponseAsync(messages, options, cancellationToken);
            var msg = response.Messages.First();

            // If the response has function calls, yield them as updates
            foreach (var content in msg.Contents)
            {
                if (content is FunctionCallContent fc)
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [fc]
                    };
                }
                else if (content is TextContent tc)
                {
                    // Stream text word by word
                    foreach (var word in tc.Text.Split(' '))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                        await Task.Delay(5, cancellationToken);
                    }
                }
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class ToolCallingFakeChatClientFactory(IServiceProvider sp) : IChatClientFactory
    {
        public string Name => "ToolCallingFakeFactory";
        public IReadOnlyList<string> Models => ["tool-calling-model"];
        public int Order => 0;

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            // Wire the real MeshPlugin tools so the agent can actually execute them
            var hub = sp.GetRequiredService<IMessageHub>();
            var meshPlugin = new MeshPlugin(hub, chat);
            var tools = meshPlugin.CreateTools(); // Get, Search, NavigateTo

            var agent = new ChatClientAgent(
                chatClient: new ToolCallingFakeChatClient(),
                instructions: config.Instructions ?? "You are a helpful test assistant that uses tools.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: tools.ToList(), loggerFactory: null, services: null);
            return Task.FromResult(agent);
        }
    }

    #endregion
}

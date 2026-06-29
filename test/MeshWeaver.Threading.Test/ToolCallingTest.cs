using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
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
/// Agent tool-calling round-trip via <see cref="ThreadSubmission.Submit"/>.
/// State is observed via <c>client.GetWorkspace().GetMeshNodeStream(path)</c>.
/// </summary>
public class ToolCallingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The auto-admin login is ObjectId "Roland", so "Roland" is the caller's own partition —
    // PartitionWriteGuardValidator exempts own-partition writes. "User/Roland" would be a bare
    // content write into the system-managed User mirror, which the guard now rejects.
    private const string ContextPath = "Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddAI()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new ToolCallingFakeChatClientFactory());
                return services;
            })
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task SubmitMessage_WithToolCalling_ExecutesSearchAndReturnsResult()
    {
        var client = GetClient();

        await NodeFactory.CreateNode(new MeshNode("test-doc", ContextPath)
        {
            Name = "Test Document",
            NodeType = "Markdown",
            Content = "Hello from test document"
        }).Should().Emit();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Tool calling test");
        var created = await NodeFactory.CreateNode(threadNode).Should().Emit();
        var threadPath = created.Path;
        Output.WriteLine($"Thread created: {threadPath}");

        var responseMsgId = await ThreadFlow.SubmitAndWait(client, threadPath,
            "Search for test documents", contextPath: ContextPath).Should().Within(30.Seconds()).Emit();

        var responseContent = await ThreadFlow.ReadMessage(client, threadPath, responseMsgId,
            m => !string.IsNullOrEmpty(m.Text) && m.Status != ThreadMessageStatus.Streaming).Should().Within(30.Seconds()).Emit();
        responseContent.Role.Should().Be("assistant");

        responseContent.Text.Should().Contain("TOOL_RESULT_RECEIVED",
            "response should include marker proving the tool was called and result received");

        Output.WriteLine($"Response: '{responseContent.Text}' ({responseContent.Text.Length} chars)");
    }

    #region Tool-Calling Fake Chat Client

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
                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant,
                        "TOOL_RESULT_RECEIVED: Based on the search results, I found the data you requested.")));
            }

            var toolCall = new FunctionCallContent("call_001", "Search",
                new Dictionary<string, object?> { ["query"] = "nodeType:Markdown" });
            var msg = new ChatMessage(ChatRole.Assistant, [toolCall]);
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            var msg = response.Messages.First();

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

    private class ToolCallingFakeChatClientFactory : IChatClientFactory
    {
        public string Name => "ToolCallingFakeFactory";
        public IReadOnlyList<string> Models => ["tool-calling-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new ToolCallingFakeChatClient(),
                instructions: config.Instructions ?? "You are a helpful test assistant that uses tools.",
                name: config.Id, description: config.Description ?? config.Id,
                tools: [], loggerFactory: null, services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    #endregion
}

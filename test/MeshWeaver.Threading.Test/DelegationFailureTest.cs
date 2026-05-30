using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Verifies that a delegation that cannot complete (broken target) doesn't
/// trap the parent thread when the user cancels. Submission goes through
/// <see cref="ThreadSubmission.Submit"/>; cancel flips
/// <c>RequestedCancellationAt</c> via the thread's MeshNode stream â€” same
/// pattern the GUI's Stop button uses.
/// </summary>
public class DelegationFailureTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new DelegatingFakeChatClientFactory());
                return services;
            })
            .AddAI()
            .AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task SubmitMessage_WithCancellation_DoesNotHangForever()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Cancellation test", "Roland");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        var threadPath = createResp.Message.Node!.Path!;

        client.SubmitMessage(
            threadPath,
            "Do something that delegates",
            contextPath: ContextPath);

        await Task.Delay(1000, ct);

        var cancelled = await workspace.GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedStatus = ThreadExecutionStatus.Cancelled } }
                : curr!)
            .FirstAsync().ToTask(ct);
        (cancelled.Content as MeshThread)?.RequestedStatus.Should().Be(ThreadExecutionStatus.Cancelled);

        var thread = await ThreadFlow.ReadThread(client, threadPath,
            t => t.Messages.Count >= 2,
            timeout: 10.Seconds()).FirstAsync().ToTask(ct);

        thread.Should().NotBeNull();
        Output.WriteLine($"Thread has {thread.Messages.Count} messages");
    }

    #region Fake delegating agent

    private class DelegatingFakeChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("DelegatingFake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("call1", "delegate_to_Worker",
                    new Dictionary<string, object?> { ["task"] = "Do some work" })]);

            await Task.Delay(100, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Delegation completed.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class DelegatingFakeChatClientFactory : IChatClientFactory
    {
        public string Name => "DelegatingFakeFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new DelegatingFakeChatClient(),
                instructions: config.Instructions ?? "You delegate everything.",
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

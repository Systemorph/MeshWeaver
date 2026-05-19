using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
/// Cancellation through the canonical path: <see cref="ThreadSubmission.Submit"/>
/// dispatches the round; the cancel trigger is a stream.Update on the thread's
/// own MeshNode (flipping <c>RequestedCancellationAt</c>) — same pattern the
/// Stop button uses in the GUI. State is observed via
/// <c>client.GetWorkspace().GetMeshNodeStream(path)</c>.
/// </summary>
public class CancelThreadExecutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new SlowChatClientFactory());
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
    public async Task CancelStream_StopsExecutionAndMarksAsCancelled()
    {
        var ct = new CancellationTokenSource(30.Seconds()).Token;
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "Cancel test", "Roland");
        var createResp = await client.Observe(new CreateNodeRequest(threadNode),
            o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask(ct);
        createResp.Message.Success.Should().BeTrue(createResp.Message.Error);
        var threadPath = createResp.Message.Node!.Path!;

        // Submit via GUI handler — server generates message ids.
        ThreadSubmission.Submit(new SubmitContext
        {
            Hub = client,
            ThreadPath = threadPath,
            UserText = "Tell me a long story",
            ContextPath = ContextPath,
        });

        // Wait for ActiveMessageId so we know which response cell to watch.
        var executing = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: true, ActiveMessageId: { Length: > 0 } })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        var responseMsgId = executing!.ActiveMessageId!;

        // Wait until the response cell shows "Generating response..." — the
        // deterministic "streaming-loop-is-armed" signal: ExecuteMessageAsync
        // emits this AFTER setting the CTS and immediately before starting the
        // streaming task. Cancelling earlier finds a null CTS → no-op.
        //
        // The server-flow path creates the response cell asynchronously after
        // flipping IsExecuting=true, so a fresh subscribe may race against the
        // CreateNode and hit a routing NotFound. RetryWhen with a short delay
        // covers that gap (same pattern the per-node hub's UpdateRemote uses
        // internally — wait for the cell to materialise).
        await Observable.Defer(() => workspace.GetMeshNodeStream($"{threadPath}/{responseMsgId}"))
            .Select(n => (n.Content as ThreadMessage)?.Text ?? "")
            .Where(t => t.StartsWith("Generating response", StringComparison.Ordinal))
            .Take(1)
            .RetryWhen(errors => errors
                .Select((ex, i) => i)
                .TakeWhile(i => i < 50)
                .SelectMany(_ => Observable.Timer(TimeSpan.FromMilliseconds(100))))
            .Timeout(15.Seconds())
            .ToTask(ct);
        var responseStream = workspace.GetMeshNodeStream($"{threadPath}/{responseMsgId}");
        Output.WriteLine("Streaming confirmed armed (response cell shows 'Generating response...')");

        // Cancel via stream.Update — same path the Stop button uses (see
        // RequestViaStreamUpdate.md). Awaiting the post-update emission asserts
        // the write actually landed on the per-thread hub.
        var cancelled = await workspace.GetMeshNodeStream(threadPath)
            .Update(curr => curr?.Content is MeshThread t
                ? curr with { Content = t with { RequestedCancellationAt = DateTime.UtcNow } }
                : curr!)
            .FirstAsync().ToTask(ct);
        (cancelled.Content as MeshThread)?.RequestedCancellationAt.Should().NotBeNull(
            "stream.Update should have stamped RequestedCancellationAt on the thread node");

        // Wait for execution to settle.
        var settled = await workspace.GetMeshNodeStream(threadPath)
            .Select(n => n.Content as MeshThread)
            .Where(t => t is { IsExecuting: false })
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        settled.Should().NotBeNull();
        Output.WriteLine($"Settled: Thread.IsExecuting={settled!.IsExecuting}");

        // Final response text — wait for any non-placeholder emission.
        var finalContent = await responseStream
            .Select(n => n.Content as ThreadMessage)
            .Where(m => m?.Text is { Length: > 0 } txt
                && !txt.StartsWith("Allocating agent", StringComparison.Ordinal)
                && !txt.StartsWith("Generating response", StringComparison.Ordinal)
                && !txt.StartsWith("Loading conversation history", StringComparison.Ordinal))
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask(ct);
        finalContent.Should().NotBeNull();
        Output.WriteLine($"Final response text: '{finalContent!.Text}'");

        var wordCount = finalContent.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        wordCount.Should().BeLessThan(50,
            "cancellation should have stopped streaming before all words were emitted");
        Output.WriteLine($"Word count: {wordCount} (expected < 50)");
    }

    #region Fake slow chat client

    /// <summary>
    /// Streams one word every 100ms, taking ~5 seconds total. Respects the
    /// CancellationToken so cancel-mid-streaming is observable in word count.
    /// </summary>
    private class SlowChatClient : IChatClient
    {
        private const string LongResponse =
            "Once upon a time in a land far away there lived a wise old wizard who knew many " +
            "secrets about the universe and spent his days reading ancient books in his tall " +
            "tower overlooking the vast ocean that stretched endlessly toward the horizon where " +
            "ships sailed carrying merchants and explorers seeking new worlds and adventures " +
            "beyond the known maps of their civilization and culture";

        public ChatClientMetadata Metadata => new("SlowProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, LongResponse)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var word in LongResponse.Split(' '))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
                await Task.Delay(100, cancellationToken);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;
        public void Dispose() { }
    }

    private class SlowChatClientFactory : IChatClientFactory
    {
        public string Name => "SlowFactory";
        public IReadOnlyList<string> Models => ["slow-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
            => new(chatClient: new SlowChatClient(),
                instructions: config.Instructions ?? "You are a slow test assistant.",
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

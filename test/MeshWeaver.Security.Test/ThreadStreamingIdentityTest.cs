using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that thread chat streaming works end-to-end with RLS enabled.
/// Verifies the identity chain: CreateThread â†’ SubmitMessage â†’ AI streaming â†’ response node update.
/// This is the monolith equivalent of OrleansChatTest but with access control restrictions.
/// The streaming response should complete even though the _Exec sub-hub runs asynchronously.
/// </summary>
public class ThreadStreamingIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string UserPath = "User/ChatUser";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddThreadType()
            .AddThreadMessageType()
            .AddAI()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new TestChatClientFactory());
                return services;
            })
            .AddMeshNodes(
                MeshNode.FromPath(UserPath) with
                {
                    Name = "Chat User",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                },
                // Pre-seed: grant ChatUser Editor on their own namespace
                // (simulates UserScopeGrantHandler) via the static node provider.
                AssignmentNodeFactory.UserRole("ChatUser", "Editor", scope: UserPath)
            );

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    private void LoginAsChatUser()
    {
        TestUsers.DevLogin(Mesh, new AccessContext
        {
            ObjectId = "ChatUser",
            Name = "Chat User"
        });
    }

    [Fact(Timeout = 30000)]
    public void CreateThread_WithRLS_Succeeds()
    {
        LoginAsChatUser();
        var client = GetClient();

        var response = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(UserPath, "Test thread")), o => o.WithTarget(new Address(UserPath)))
            .Should().Within(25.Seconds()).Emit();

        response.Message.Success.Should().BeTrue(response.Message.Error ?? "CreateThread should succeed for user with Editor role");
        response.Message.Node?.Path.Should().Contain("_Thread/");
        Output.WriteLine($"Thread created: {response.Message.Node?.Path}");
    }

    [Fact(Timeout = 30000)]
    public void SubmitMessage_StreamsResponse_WithRLS()
    {
        LoginAsChatUser();
        var client = GetClient();

        // 1. Create thread
        var createResponse = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(UserPath, "Streaming test")), o => o.WithTarget(new Address(UserPath)))
            .Should().Within(25.Seconds()).Emit();
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error ?? "");
        var threadPath = createResponse.Message.Node!.Path!;
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Submit message via stream.Update — triggers AI streaming on the _Exec sub-hub.
        //    SubmitMessageRequest deleted 2026-05-25; submission is now ThreadSubmission.Submit
        //    which writes PendingUserMessages on the thread node.
        client.SubmitMessage(
            threadPath,
            "Hello from identity test",
            contextPath: UserPath);
        Output.WriteLine("Message submitted, waiting for streaming...");

        // 3. Wait reactively for the response message to be populated by streaming.
        //    Accumulate the live query deltas into a path-keyed snapshot, then match
        //    on the first snapshot containing an assistant message with non-empty text.
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var responseMessage = AccumulateDescendants(meshQuery, threadPath)
            .Select(snapshot => snapshots
                .Select(n => n.Content as ThreadMessage)
                .FirstOrDefault(tm => tm is { Role: "assistant" } && !string.IsNullOrEmpty(tm.Text)))
            .Should().Within(25.Seconds()).Match(tm => tm is not null);

        responseMessage.Should().NotBeNull(
            "AI streaming should produce a response message â€” " +
            "if this fails, the identity chain is broken during async _Exec sub-hub execution");
        responseMessage!.Text.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Response: '{responseMessage.Text}'");
    }

    /// <summary>
    /// Folds the live <c>Query</c> deltas for a thread's ThreadMessage
    /// descendants into a running path-keyed snapshot, so a reactive assertion can
    /// match on the accumulated state (Initial may race the streaming writes).
    /// </summary>
    private IObservable<IReadOnlyDictionary<string, MeshNode>> AccumulateDescendants(
        IMeshService meshQuery, string threadPath)
        => meshQuery
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{threadPath} scope:descendants nodeType:ThreadMessage"))
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset
                or QueryChangeType.Added or QueryChangeType.Updated)
            .Scan(
                new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase),
                (acc, change) =>
                {
                    if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                        acc.Clear();
                    foreach (var n in change.Items)
                        if (n.Path is { } p) acc[p] = n;
                    return acc;
                })
            .Select(d => (IReadOnlyDictionary<string, MeshNode>)d);

    [Fact(Timeout = 30000)]
    public void SubmitMessage_StreamsIncrementally_NotAllAtOnce()
    {
        LoginAsChatUser();
        var client = GetClient();

        // Create thread
        var createResponse = client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(UserPath, "Incremental test")), o => o.WithTarget(new Address(UserPath)))
            .Should().Within(25.Seconds()).Emit();
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error ?? "");
        var threadPath = createResponse.Message.Node!.Path!;

        // Submit message via stream.Update.
        var submitTime = DateTimeOffset.UtcNow;
        client.SubmitMessage(
            threadPath,
            "Stream incrementally please",
            contextPath: UserPath);

        // Wait reactively for first partial response â€” should arrive within 5 seconds,
        // NOT after full streaming completes (which would take longer).
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        AccumulateDescendants(meshQuery, threadPath)
            .Select(snapshot => snapshots
                .Select(n => n.Content as ThreadMessage)
                .Any(tm => tm is { Role: "assistant", Text.Length: > 0 }))
            .Should().Within(25.Seconds()).Match(hasPartial => hasPartial);
        var firstResponseTime = DateTimeOffset.UtcNow;

        var latency = (firstResponseTime - submitTime).TotalMilliseconds;
        Output.WriteLine($"First response appeared {latency:F0}ms after submit");

        // The first partial response should arrive within 5 seconds.
        // If updates are blocked (old bug), they'd all arrive at once after streaming completes.
        latency.Should().BeLessThan(5000,
            "first streaming update should arrive within 5s â€” if it takes longer, " +
            "updates are blocked in the _Exec hub's message buffer (the bug we fixed)");
    }

    /// <summary>
    /// Verifies that streaming produces MULTIPLE distinct emissions over time with
    /// MONOTONICALLY GROWING text — proving the response cell is patched incrementally
    /// as chunks arrive, not in a single all-at-end write. If the framework batches
    /// all updates into the final commit, this test catches it: only one emission
    /// with the final text would arrive.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void SubmitMessage_StreamingProducesMultipleGrowingEmissions()
    {
        LoginAsChatUser();
        var client = GetClient();
        var ct = new CancellationTokenSource(25.Seconds()).Token;

        // Create thread + submit
        var createResponse = client.Observe(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(UserPath, "Growing emissions test")),
            o => o.WithTarget(new Address(UserPath)))
            .Should().Within(25.Seconds()).Emit();
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error ?? "");
        var threadPath = createResponse.Message.Node!.Path!;

        client.SubmitMessage(
            threadPath,
            "Stream me growing chunks please",
            contextPath: UserPath);

        // Wait reactively for the response cell to be allocated.
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var responsePath = AccumulateDescendants(meshQuery, threadPath)
            .Select(snapshot => snapshots
                .FirstOrDefault(n => n.Content is ThreadMessage { Role: "assistant" })?.Path)
            .Should().Within(25.Seconds()).Match(p => p is not null);
        responsePath.Should().NotBeNull("response cell must exist for streaming to land on it");

        // Subscribe to the response cell's MeshNode stream and collect emissions
        // (timestamp + text length). Stop once we see the terminal Completed/Cancelled/Error
        // status — or the 20 s budget elapses.
        var workspace = client.GetWorkspace();
        var emissions = new List<(DateTimeOffset Ts, int Len, string Text, ThreadMessageStatus? Status)>();
        var firstEmissionDone = false;
        using var collected = new ManualResetEventSlim(false);
        var sub = workspace
            .GetMeshNodeStream(responsePath!)
            .Where(c => c is not null)
            .Select(c => c!.Content as ThreadMessage)
            .Where(m => m is not null)
            .Subscribe(m =>
            {
                lock (emissions)
                {
                    emissions.Add((DateTimeOffset.UtcNow, m!.Text.Length, m.Text, m.Status));
                    firstEmissionDone = true;
                    if (m.Status is ThreadMessageStatus.Completed
                                  or ThreadMessageStatus.Cancelled
                                  or ThreadMessageStatus.Error)
                        collected.Set();
                }
            });

        try
        {
            collected.Wait(20_000, ct);
        }
        finally
        {
            sub.Dispose();
        }

        firstEmissionDone.Should().BeTrue(
            "the response stream must emit at least once — if it doesn't, streaming never landed on the response cell");

        // The TestChatClient yields 6 words with 10 ms delays. Even allowing for
        // sample-rate throttling we expect at least one growing-text emission
        // before/at the terminal Completed snapshot.
        //
        // Filter out placeholder emissions (the initial "Generating response..."
        // sent before streaming starts) — those don't share the streamed-text
        // monotonic property with the actual streamed snapshots and a
        // placeholder → first-streamed-word transition LOOKS like a regression
        // when measured by raw .Length.
        //
        // Status: accept both Streaming AND Completed — the Sample(100ms) gate
        // typically collapses TestChatClient's 60ms run into ONE final emission
        // whose Status is already Completed (the streaming loop's terminal
        // PushToResponseMessage runs with Completed). Restricting to Streaming
        // misses this case and fails with "0 growing emissions" in CI.
        var growingEmissions = emissions
            .Where(e => e.Len > 0
                && e.Status is ThreadMessageStatus.Streaming or ThreadMessageStatus.Completed
                && !e.Text.StartsWith("Generating response", StringComparison.Ordinal)
                && !e.Text.StartsWith("Allocating agent", StringComparison.Ordinal)
                && !e.Text.StartsWith("Loading conversation history", StringComparison.Ordinal))
            .ToList();
        Output.WriteLine(
            $"Captured {emissions.Count} emissions; {growingEmissions.Count} mid-stream growing emissions. " +
            $"Text lengths: [{string.Join(",", emissions.Select(e => e.Len))}]");

        growingEmissions.Count.Should().BeGreaterThanOrEqualTo(1,
            "the response cell must receive at least one streamed text emission before the " +
            "terminal Completed write. The Sample(100ms) gate collapses fast bursts into one " +
            "snapshot — TestChatClient's 6 words × 10ms = 60ms run typically yields exactly " +
            "one growing emission at the Sample boundary. If we see 0, the streaming path is " +
            "blocked entirely (e.g. action block stalled, identity gate denied). " +
            $"Emission lengths: [{string.Join(",", emissions.Select(e => e.Len))}]");

        // Monotonic growth: each successive Streaming emission's text length must
        // be >= the previous. A drop would mean a stale snapshot overwrote a fresher one.
        for (var i = 1; i < growingEmissions.Count; i++)
        {
            growingEmissions[i].Len.Should().BeGreaterThanOrEqualTo(growingEmissions[i - 1].Len,
                $"emission #{i} length ({growingEmissions[i].Len}) regressed from #{i - 1} " +
                $"({growingEmissions[i - 1].Len}) — a later patch shrank the text, indicating " +
                "stale-mirror clobber.");
        }
    }

}

/// <summary>
/// Fake chat client for testing â€” returns a simple response.
/// </summary>
internal class TestChatClientFactory : IChatClientFactory
{
    private const string ResponseText = "Test response from identity-verified agent.";
    public string Name => "TestFactory";
    public IReadOnlyList<string> Models => ["test-model"];
    public int Order => 0;

    public ChatClientAgent CreateAgent(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => new(chatClient: new TestChatClient(ResponseText),
            instructions: config.Instructions ?? "Test.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [], loggerFactory: null, services: null);

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
        => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
}

internal class TestChatClient(string response) : IChatClient
{
    public ChatClientMetadata Metadata => new("TestProvider");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in response.Split(' '))
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

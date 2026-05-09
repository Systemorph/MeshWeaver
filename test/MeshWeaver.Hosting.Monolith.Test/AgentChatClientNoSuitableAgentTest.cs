using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Reproduces the production "No suitable agent found to handle the request"
/// failure: AgentChatClient runs on the THREAD hub (per-node, child of the
/// mesh hub), subscribes to <see cref="AgentPickerProjection.ObserveAgents"/>,
/// and depends on the synced query producing a non-empty agent set within
/// the readiness window. If the projection emits empty (or never emits)
/// in the thread-hub scope, <c>WhenInitialized</c> fires with
/// <c>loadedAgents.Count == 0</c>, <c>SelectAgent</c> returns null, and the
/// chat surfaces the canonical error string.
///
/// <para>This test stands the chat client up against a hosted sub-hub
/// (PortalApplication.Hub shape) with a stub <see cref="IChatClientFactory"/>
/// so the full Initialize → SelectAgent path is exercised without going to
/// a real Anthropic / OpenAI endpoint.</para>
/// </summary>
public class AgentChatClientNoSuitableAgentTest : MonolithMeshTestBase
{
    public AgentChatClientNoSuitableAgentTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:Models:0"] = "claude-opus-4-6",
                ["Anthropic:Models:1"] = "claude-sonnet-4-6",
                ["Anthropic:Models:2"] = "claude-haiku-4-5",
            })
            .Build();

        var persistence = new InMemoryPersistenceService();

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IConfiguration>(config);
                services.AddInMemoryPersistence(persistence);
                // Stub factory matching the Anthropic models above so
                // CreateAgentsSync can build ChatClientAgent instances.
                services.AddSingleton<IChatClientFactory, StubChatClientFactory>();
                return services;
            })
            .ConfigureHub(c => c.AddData())
            .AddAI();
    }

    [Fact]
    public async Task AgentChatClient_OnHostedSubHub_DoesNotReturn_NoSuitableAgent()
    {
        // Hosted sub-hub mirrors the per-thread hub shape — child of the mesh
        // hub via Mesh.GetHostedHub, same DI scope inheritance prod uses.
        var subHub = Mesh.GetHostedHub(
            new Address("portal", "test-user"),
            c => c.AddData());

        var client = new AgentChatClient(subHub.ServiceProvider);
        client.SetThreadId(subHub.Address.Path);
        client.Initialize(contextPath: null, modelName: "claude-haiku-4-5");

        // Wait for the picker emission to land (Initialize is sync; agents
        // populate asynchronously via the AgentPickerProjection subscription).
        await ((IObservable<AgentChatClient>)client.WhenInitialized)
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        var agents = await client.GetOrderedAgentsAsync();
        agents.Should().NotBeEmpty(
            "AgentChatClient.Initialize → AgentPickerProjection.ObserveAgents on a "
            + "hosted sub-hub MUST produce the built-in agent catalog. Empty here "
            + "is the precondition for the production 'No suitable agent found' bug.");

        // Drive a chat round through GetStreamingResponseAsync — same path
        // ExecuteMessageAsync hits. If SelectAgent returns null we get the
        // exact error string the user reported in production.
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello?") };
        var firstUpdate = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            firstUpdate += ExtractText(update);
            if (firstUpdate.Length > 0) break;
        }

        // 🚨 Production-actionable error required: when ALL agents fail to
        // materialise because the registered factory throws (typical
        // misconfiguration: AzureClaude Endpoint/ApiKey unset), the chat
        // response MUST surface the underlying exception message — not the
        // useless generic "No suitable agent found" string. The fix below
        // captures the per-agent failure in CreateAgentsSync and surfaces it
        // when SelectAgent's null-fallback path runs.
        firstUpdate.Should().NotContain("No suitable agent found to handle the request",
            "the canonical UX failure — when factory throws on every agent, the "
            + "user used to see this opaque message instead of the actual config "
            + "error. The fix surfaces the underlying exception in the response.");
        firstUpdate.Should().Contain("Endpoint is required",
            "the chat response must surface the actual factory exception so users "
            + "know to set Anthropic:Endpoint / ApiKey in their config.");
    }

    private static string ExtractText(ChatResponseUpdate u)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in u.Contents)
        {
            if (c is TextContent t) sb.Append(t.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Stub <see cref="IChatClientFactory"/> that THROWS in <c>CreateAgent</c>
    /// — mimics the production failure mode where the Anthropic factory is
    /// registered (matches "claude-*" via <c>Supports</c>) but throws
    /// <c>"Endpoint is required"</c> / <c>"ApiKey is required"</c> when actually
    /// constructing the chat client (because the user-secrets / appsettings
    /// section was misconfigured). <c>CreateAgentsSync</c> swallows the
    /// per-agent exception → <c>agents</c> dict stays empty →
    /// <c>SelectAgent</c> returns null → user sees the canonical error.
    /// </summary>
    private sealed class StubChatClientFactory : IChatClientFactory
    {
        public string Name => "Stub Anthropic";
        public IReadOnlyList<string> Models =>
            ["claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5"];
        public int Order => 0;
        public bool Supports(string modelName) => !string.IsNullOrEmpty(modelName);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
        {
            // Production-equivalent: AzureClaudeChatClientAgentFactory.CreateChatClient
            // throws this exact message when Anthropic:Endpoint isn't set.
            throw new InvalidOperationException(
                "Endpoint is required in AzureClaudeConfiguration. Set Anthropic:Endpoint via user-secrets or appsettings.");
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "stub");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

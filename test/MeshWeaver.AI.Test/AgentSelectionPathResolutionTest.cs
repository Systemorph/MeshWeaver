#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Reproduces bug (b): selecting an agent from the chat composer's mesh-node picker
/// silently mis-resolves / fails when the picked agent path is SPACE-scoped and its
/// last path segment COLLIDES with a built-in agent of the same name, or when the
/// picked path is UNKNOWN (the atioz Loki scenario:
/// <c>AgentName = "AgenticPension/Agent/Datenextraktion"</c> that no longer resolves).
///
/// <para>The picker stores the node's FULL PATH. Resolution used to collapse that to the
/// bare last segment (<see cref="SelectionId.IdOf"/>) and key the agents dictionary by
/// <see cref="AgentConfiguration.Id"/> = last segment — so two agents with the same last
/// segment in different namespaces collided to one dictionary slot, and the full-path
/// selection could not pick the intended one. The fix resolves by FULL PATH and degrades
/// gracefully when the path is unknown.</para>
/// </summary>
public class AgentSelectionPathResolutionTest : AITestBase
{
    public AgentSelectionPathResolutionTest(ITestOutputHelper output) : base(output) { }

    // Distinct instruction strings let the fake client echo back WHICH config won
    // resolution — that is how we assert the correct agent was selected.
    private const string BuiltInInstructions = "BUILTIN-CODER-INSTRUCTIONS";
    private const string SpaceInstructions = "SPACE-DATENEXTRAKTION-INSTRUCTIONS";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new EchoInstructionsFactory());
                return services;
            });

    /// <summary>
    /// A fake chat client that echoes the agent INSTRUCTIONS it was created with, so the
    /// test can prove which AgentConfiguration was resolved for the selection.
    /// </summary>
    private sealed class EchoInstructionsChatClient(string instructions) : IChatClient
    {
        public ChatClientMetadata Metadata => new("Echo");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, instructions)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, instructions);
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class EchoInstructionsFactory : IChatClientFactory
    {
        public string Name => "EchoFactory";
        public IReadOnlyList<string> Models => ["fake-model"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => new(
                chatClient: new EchoInstructionsChatClient(config.Instructions ?? "(none)"),
                instructions: config.Instructions ?? "(none)",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => Task.FromResult(CreateAgent(config, chat, existingAgents, hierarchyAgents, modelName));
    }

    private static AgentDisplayInfo Agent(string path, string instructions)
    {
        var id = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return new AgentDisplayInfo
        {
            Name = id,
            Path = path,
            Description = id,
            AgentConfiguration = new AgentConfiguration { Id = id, Instructions = instructions },
        };
    }

    private static async Task<string> RunAndCaptureAsync(AgentChatClient client)
    {
        var sb = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// RED before the fix: two agents whose last path segment collides
    /// (<c>Agent/Coder</c> built-in and <c>AgenticPension/Agent/Coder</c> space) load into
    /// the same dictionary slot keyed by the bare id "Coder". Selecting the SPACE agent by
    /// its full path resolves the wrong (built-in) agent because the selection collapses to
    /// "Coder" and the built-in won the slot.
    /// </summary>
    [Fact]
    public async Task SelectSpaceScopedAgent_ByFullPath_ResolvesThatAgent_NotTheCollidingBuiltIn()
    {
        var client = new AgentChatClient(Mesh.ServiceProvider);
        // Deliberate last-segment collision across namespaces. Drives the real
        // production code path (the synced-query Subscribe callback calls ApplyAgents).
        client.ApplyAgents(
            new[]
            {
                Agent("Agent/Coder", BuiltInInstructions),
                Agent("AgenticPension/Agent/Coder", SpaceInstructions),
            },
            contextPath: null);

        // The picker stores the FULL node path; that is what the composer flows to the client.
        client.SetSelectedAgent("AgenticPension/Agent/Coder");

        var response = await RunAndCaptureAsync(client);

        response.Should().Contain(SpaceInstructions,
            "selecting the space-scoped agent by its full path must resolve THAT agent");
    }

    /// <summary>
    /// An UNKNOWN / mismatched agent path (the atioz Loki scenario: a stale
    /// <c>AgenticPension/Agent/Datenextraktion</c> that no longer resolves) must degrade
    /// gracefully — the client surfaces a clean "no agent found" message instead of
    /// silently routing to an arbitrary built-in agent or throwing.
    /// </summary>
    [Fact]
    public async Task SelectUnknownAgentPath_DegradesGracefully_DoesNotSilentlyPickAnotherAgent()
    {
        var client = new AgentChatClient(Mesh.ServiceProvider);
        client.ApplyAgents(new[] { Agent("Agent/Coder", BuiltInInstructions) }, contextPath: null);

        // Path that does not match any loaded agent.
        client.SetSelectedAgent("AgenticPension/Agent/Datenextraktion");

        var response = await RunAndCaptureAsync(client);

        response.Should().NotContain(BuiltInInstructions,
            "an unknown explicit selection must NOT silently fall through to a different agent");
        response.Should().Contain("Datenextraktion",
            "the unknown-agent message should name the agent the user asked for");
    }
}

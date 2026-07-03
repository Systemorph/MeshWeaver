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
    private const string BuiltInInstructions = "BUILTIN-ANALYST-INSTRUCTIONS";
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
    /// (<c>Agent/Analyst</c> built-in and <c>AgenticPension/Agent/Analyst</c> space) load into
    /// the same dictionary slot keyed by the bare id "Analyst". Selecting the SPACE agent by
    /// its full path resolves the wrong (built-in) agent because the selection collapses to
    /// "Analyst" and the built-in won the slot.
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
                Agent("Agent/Analyst", BuiltInInstructions),
                Agent("AgenticPension/Agent/Analyst", SpaceInstructions),
            },
            contextPath: null);

        // The picker stores the FULL node path; that is what the composer flows to the client.
        client.SetSelectedAgent("AgenticPension/Agent/Analyst");

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
        client.ApplyAgents(new[] { Agent("Agent/Analyst", BuiltInInstructions) }, contextPath: null);

        // Path that does not match any loaded agent.
        client.SetSelectedAgent("AgenticPension/Agent/Datenextraktion");

        var response = await RunAndCaptureAsync(client);

        response.Should().NotContain(BuiltInInstructions,
            "an unknown explicit selection must NOT silently fall through to a different agent");
        response.Should().Contain("Datenextraktion",
            "the unknown-agent message should name the agent the user asked for");
        response.Should().Contain("was not found among the available agents",
            "a genuinely unmatched selection IS an agent-not-found failure");
    }

    /// <summary>
    /// #201: with an EMPTY agent catalog the failure is "no agents loaded in this
    /// context", never "your selection was moved or renamed — pick another from the
    /// list": there is no list. The stale-selection wording dead-ended users on the
    /// deployed portal ("available agents ([])" with nothing to pick); the empty-set
    /// message must name the real condition (catalog not emitted / not visible) and
    /// the recovery (retry; check agent visibility).
    /// </summary>
    [Fact]
    public async Task SelectAgent_WithEmptyCatalog_ReportsEmptyCatalog_NotStaleSelection()
    {
        var client = new AgentChatClient(Mesh.ServiceProvider);
        client.ApplyAgents(Array.Empty<AgentDisplayInfo>(), contextPath: null);

        client.SetSelectedAgent("Agent/Assistant");

        var response = await RunAndCaptureAsync(client);

        response.Should().Contain("Agent/Assistant",
            "the message should name the agent the user asked for");
        response.Should().Contain("agent catalog is empty",
            "an empty catalog is a load/visibility condition, not a stale selection");
        response.Should().NotContain("pick another agent from the list",
            "there is nothing to pick from — the stale-selection advice is a dead end");
        response.Should().NotContain("may have been moved, renamed",
            "blaming the selection is wrong when zero agents resolved");
    }
}

/// <summary>
/// The MATCHED-agent failure surface: when the selected agent IS in the loaded list but
/// cannot be built (the factory throws for the selected model), the chat output must state
/// the REAL failure — agent found, creation/model failed — never the misleading
/// "agent 'X' was not found among the available agents ([X, …])" observed live on the
/// e2e portal 2026-07-01 (the agent was FIRST in the printed list).
/// </summary>
public class AgentSelectionMatchedButUnbuildableTest : AITestBase
{
    public AgentSelectionMatchedButUnbuildableTest(ITestOutputHelper output) : base(output) { }

    private const string CreationFailure = "ApiKey is missing for model 'qwen-small'";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory>(new ThrowingFactory());
                return services;
            });

    /// <summary>A factory whose CreateAgent always throws — the "provider unconfigured / credentials missing" shape.</summary>
    private sealed class ThrowingFactory : IChatClientFactory
    {
        public string Name => "ThrowingFactory";
        public IReadOnlyList<string> Models => ["qwen-small"];
        public int Order => 0;

        public ChatClientAgent CreateAgent(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => throw new InvalidOperationException(CreationFailure);

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config, IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents, string? modelName = null)
            => throw new InvalidOperationException(CreationFailure);
    }

    [Fact]
    public async Task MatchedAgent_WhoseCreationFails_SurfacesTheCreationFailure_NotAgentNotFound()
    {
        var client = new AgentChatClient(Mesh.ServiceProvider);
        // The batch build fails for every agent (ThrowingFactory) → agents dict stays empty,
        // which is exactly the state that used to mis-report "agent not found".
        client.ApplyAgents(
            new[]
            {
                new AgentDisplayInfo
                {
                    Name = "Assistant",
                    Path = "Agent/Assistant",
                    Description = "Assistant",
                    AgentConfiguration = new AgentConfiguration { Id = "Assistant", Instructions = "assist" },
                },
            },
            contextPath: null);

        // The composer's picker form: the full node path.
        client.SetSelectedAgent("Agent/Assistant");

        var sb = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);
        }
        var response = sb.ToString();

        response.Should().NotContain("was not found among the available agents",
            "the agent WAS matched — reporting it as not-found masks the real failure");
        response.Should().Contain("Agent/Assistant",
            "the error must name the agent that was matched");
        response.Should().Contain(CreationFailure,
            "the error must carry the factory's real failure so the operator can act on it");
        response.Should().Contain("ThrowingFactory",
            "the error must name the factory that failed");
    }
}

/// <summary>
/// The MATCHED-agent / NO-factory failure surface: with no <see cref="IChatClientFactory"/>
/// registered at all, selecting an agent that IS loaded must report the factory/model gap
/// ("no chat-client factory resolves model …") — never "agent not found".
/// </summary>
public class AgentSelectionNoFactoryTest : AITestBase
{
    public AgentSelectionNoFactoryTest(ITestOutputHelper output) : base(output) { }

    // Deliberately NO IChatClientFactory registration — base AddAI() registers none.

    [Fact]
    public async Task MatchedAgent_WithNoFactoryForModel_ReportsFactoryModelGap_NotAgentNotFound()
    {
        var client = new AgentChatClient(Mesh.ServiceProvider);
        client.ApplyAgents(
            new[]
            {
                new AgentDisplayInfo
                {
                    Name = "Assistant",
                    Path = "Agent/Assistant",
                    Description = "Assistant",
                    AgentConfiguration = new AgentConfiguration { Id = "Assistant", Instructions = "assist" },
                },
            },
            contextPath: null);

        client.SetSelectedAgent("Agent/Assistant");

        var sb = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], TestContext.Current.CancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);
        }
        var response = sb.ToString();

        response.Should().NotContain("was not found among the available agents",
            "the agent WAS matched — the failure is factory/model resolution, not agent lookup");
        response.Should().Contain("Agent/Assistant",
            "the error must name the matched agent");
        response.Should().Contain("no chat-client factory resolves model",
            "the error must say the failure is the chat-client factory/model resolution");
    }
}

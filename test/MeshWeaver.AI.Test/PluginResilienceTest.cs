#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Plugins;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the plugin-failure blast radius (memex prod, 2026-08-21): a registered
/// <see cref="IAgentPlugin"/> whose constructor dependency is NOT registered — the concrete case
/// was the Mail module's ExecutiveAssistantPlugin needing IEaGraphAuth, which the host registers
/// only when Email:Enabled — must cost the agent that plugin's tools, never the agent.
/// <c>GetServices&lt;IAgentPlugin&gt;()</c> activates EVERY registered implementation, so before the
/// fix ONE unresolvable plugin threw for every agent referencing ANY custom plugin, and the
/// selected agent (Assistant) failed outright: chat down.
/// </summary>
[Collection("PluginResilienceTests")]
public class PluginResilienceTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public PluginResilienceTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IChatClientFactory, StubChatClientFactory>();
                // The healthy plugin the agent actually references…
                services.AddSingleton<IAgentPlugin, WorkingPlugin>();
                // …and the poisoned registration: its ctor dependency is deliberately NOT
                // registered, so ANY GetServices<IAgentPlugin>() activation throws.
                services.AddSingleton<IAgentPlugin, UnresolvablePlugin>();
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());

    [Fact]
    public void UnresolvablePluginRegistration_CostsThePlugin_NeverTheAgent()
    {
        var factory = Mesh.ServiceProvider.GetServices<IChatClientFactory>()
            .OfType<StubChatClientFactory>().Single();
        var chat = new AgentChatClient(Mesh.ServiceProvider);

        var config = new AgentConfiguration
        {
            Id = "Resilient",
            Instructions = "Test agent",
            Plugins = [new AgentPluginReference { Name = "Working" }],
        };

        // Before the fix this threw
        //   "An exception was thrown while activating …IAgentPlugin[] -> …UnresolvablePlugin"
        // out of ResolvePluginTools and the agent was never built.
        var agent = factory.CreateAgent(
            config, chat,
            ImmutableDictionary<string, ChatClientAgent>.Empty,
            [config]);

        Assert.NotNull(agent);
        Assert.Equal("Resilient", agent.Name);
    }

    internal class StubChatClientFactory(IMessageHub hub) : ChatClientAgentFactory(hub)
    {
        public override string Name => "StubFactory";
        public override IReadOnlyList<string> Models => ["stub-model"];
        public override int Order => 0;
        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig) => new StubChatClient();
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private interface INeverRegistered
    {
        string Value { get; }
    }

    private sealed class UnresolvablePlugin(INeverRegistered dependency) : IAgentPlugin
    {
        public string Name => "Broken";
        public IEnumerable<AITool> CreateTools() => [AIFunctionFactory.Create(() => dependency.Value, "broken_tool")];
    }

    private sealed class WorkingPlugin : IAgentPlugin
    {
        public string Name => "Working";
        public IEnumerable<AITool> CreateTools() => [AIFunctionFactory.Create(() => "ok", "working_tool")];
    }
}

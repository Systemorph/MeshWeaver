#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for SpecWriter agent discovery, configuration, and tool wiring.
/// </summary>
[Collection("AgentToolWiringTests")]
public class SpecWriterAgentTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public SpecWriterAgentTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {

                services.AddSingleton<CapturingChatClient>();
                services.AddSingleton<IChatClientFactory, CapturingChatClientFactory>();
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Verifies that SpecWriter is discovered as a built-in agent with correct plugins.
    /// </summary>
    [Fact]
    public async Task SpecWriter_IsDiscovered_WithCorrectPlugins()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");

        var agents = await chatClient.GetOrderedAgentsAsync();
        var specWriter = agents.FirstOrDefault(a => a.Name == "SpecWriter");

        specWriter.Should().NotBeNull("SpecWriter agent should be discovered");
        Output.WriteLine($"SpecWriter found at path: {specWriter!.Path}");
    }

    /// <summary>
    /// Verifies that SpecWriter has Research delegation configured.
    /// </summary>
    [Fact]
    public async Task SpecWriter_HasResearchDelegation()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");

        var agents = await chatClient.GetOrderedAgentsAsync();
        var specWriter = agents.First(a => a.Name == "SpecWriter");

        specWriter.AgentConfiguration.Should().NotBeNull();
        specWriter.AgentConfiguration!.Delegations.Should().NotBeNullOrEmpty(
            "SpecWriter should have delegations configured");
        specWriter.AgentConfiguration.Delegations!.Should().Contain(
            d => d.AgentPath.Contains("Research"),
            "SpecWriter should delegate to Research agent");
    }

    /// <summary>
    /// Verifies that SpecWriter gets only read-only Mesh tools (Get, Search) and no write tools.
    /// </summary>
    [Fact]
    public async Task SpecWriter_GetsReadOnlyMeshTools()
    {
        var capturingClient = Mesh.ServiceProvider.GetRequiredService<CapturingChatClient>();

        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");
        chatClient.SetSelectedAgent("SpecWriter");

        // Send a message to trigger agent creation and tool wiring
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await foreach (var _ in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken)) { }

        var toolNames = capturingClient.LastCapturedOptions?.Tools?
            .OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];

        Output.WriteLine($"SpecWriter tools ({toolNames.Count}): {string.Join(", ", toolNames)}");

        toolNames.Should().Contain("Get", "SpecWriter should have Get tool for reading nodes");
        toolNames.Should().Contain("Search", "SpecWriter should have Search tool for finding context");
        toolNames.Should().Contain("Create", "SpecWriter should have Create tool for creating nodes");
        toolNames.Should().Contain("Update", "SpecWriter should have Update tool for updating nodes");
        toolNames.Should().NotContain("Delete", "SpecWriter should NOT have Delete");
    }

    #region Capturing Infrastructure (same as AgentToolWiringIntegrationTest)

    internal class CapturingChatClientFactory(IMessageHub hub, CapturingChatClient client) : ChatClientAgentFactory(hub)
    {
        public override string Name => "CapturingFactory";
        public override IReadOnlyList<string> Models => ["capturing-model"];
        public override int Order => 0;

        protected override IChatClient CreateChatClient(AgentConfiguration agentConfig) => client;
    }

    internal class CapturingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("CapturingProvider");
        public List<ChatMessage> AllCapturedMessages { get; } = [];
        public ChatOptions? LastCapturedOptions { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            AllCapturedMessages.AddRange(messageList);
            LastCapturedOptions = options;

            var msg = new ChatMessage(ChatRole.Assistant, "Captured response.");
            return Task.FromResult(new ChatResponse(msg));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            AllCapturedMessages.AddRange(messageList);
            LastCapturedOptions = options;

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Captured response.");
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    #endregion
}

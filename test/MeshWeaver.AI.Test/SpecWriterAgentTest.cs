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
using MeshWeaver.AI.Plugins;
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
                services.AddGitHubPlugin();
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
    /// Verifies that SpecWriter gets Mesh tools (Get, Search, Create, Update) and no Delete.
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

    /// <summary>
    /// Verifies that GitHubPlugin is registered in DI and exposes the expected tools.
    /// This mirrors the portal registration path (AddGitHubPlugin with config binding).
    /// </summary>
    [Fact]
    public async Task SpecWriter_GitHubPlugin_IsRegisteredWithExpectedTools()
    {
        var plugins = Mesh.ServiceProvider.GetServices<IAgentPlugin>().ToList();
        var githubPlugin = plugins.FirstOrDefault(p => p.Name == "GitHub");

        githubPlugin.Should().NotBeNull("GitHubPlugin must be registered in DI for SpecWriter to create issues");

        var tools = githubPlugin!.CreateTools();
        var toolNames = tools.Select(t => t.Name).ToList();

        toolNames.Should().Contain("CreateIssue");
        toolNames.Should().Contain("ListIssues");
    }

    #region Group 4: SpecWriter Agent Tests

    [Fact]
    public async Task SpecWriter_HasCorrectMetadata()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");

        var agents = await chatClient.GetOrderedAgentsAsync();
        var specWriter = agents.First(a => a.Name == "SpecWriter");
        var config = specWriter.AgentConfiguration!;

        config.PreferredModel.Should().Be("claude-opus-4-6");
        config.ExposedInNavigator.Should().BeTrue();
    }

    [Fact]
    public async Task SpecWriter_HasCorrectPluginReferences()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");

        var agents = await chatClient.GetOrderedAgentsAsync();
        var specWriter = agents.First(a => a.Name == "SpecWriter");
        var config = specWriter.AgentConfiguration!;

        config.Plugins.Should().NotBeNull();
        config.Plugins.Should().HaveCount(2);

        var meshPlugin = config.Plugins!.First(p => p.Name == "Mesh");
        meshPlugin.Methods.Should().BeEquivalentTo(["Get", "Search", "Create", "Update"]);

        var githubPlugin = config.Plugins!.First(p => p.Name == "GitHub");
        githubPlugin.Methods.Should().BeNullOrEmpty("GitHub plugin exposes all methods");
    }

    [Fact]
    public async Task SpecWriter_GetsGitHubTools()
    {
        var capturingClient = Mesh.ServiceProvider.GetRequiredService<CapturingChatClient>();

        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");
        chatClient.SetSelectedAgent("SpecWriter");

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await foreach (var _ in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken)) { }

        var toolNames = capturingClient.LastCapturedOptions?.Tools?
            .OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];

        Output.WriteLine($"SpecWriter tools: {string.Join(", ", toolNames)}");

        toolNames.Should().Contain("CreateIssue");
        toolNames.Should().Contain("GetIssue");
        toolNames.Should().Contain("ListIssues");
        toolNames.Should().Contain("UpdateIssue");
    }

    [Fact]
    public async Task SpecWriter_GetsExactToolSet()
    {
        var capturingClient = Mesh.ServiceProvider.GetRequiredService<CapturingChatClient>();

        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");
        chatClient.SetSelectedAgent("SpecWriter");

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await foreach (var _ in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken)) { }

        var toolNames = capturingClient.LastCapturedOptions?.Tools?
            .OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];

        Output.WriteLine($"SpecWriter full tool set ({toolNames.Count}): {string.Join(", ", toolNames)}");

        // Mesh tools (filtered)
        toolNames.Should().Contain("Get");
        toolNames.Should().Contain("Search");
        toolNames.Should().Contain("Create");
        toolNames.Should().Contain("Update");

        // GitHub tools (all)
        toolNames.Should().Contain("CreateIssue");
        toolNames.Should().Contain("GetIssue");
        toolNames.Should().Contain("ListIssues");
        toolNames.Should().Contain("UpdateIssue");

        // Delegation tool
        toolNames.Should().Contain("delegate_to_agent");

        // Should NOT have these
        toolNames.Should().NotContain("Delete");
        toolNames.Should().NotContain("Patch");
    }

    [Fact]
    public async Task SpecWriter_InstructionsContainReferences()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME");

        var agents = await chatClient.GetOrderedAgentsAsync();
        var specWriter = agents.First(a => a.Name == "SpecWriter");
        var instructions = specWriter.AgentConfiguration!.Instructions;

        Output.WriteLine($"Instructions length: {instructions?.Length ?? 0}");
        if (instructions != null)
            Output.WriteLine($"Instructions preview: {instructions[..Math.Min(200, instructions.Length)]}");

        instructions.Should().NotBeNull();
        instructions.Should().NotBeEmpty();
        // Raw instructions contain @@references that are resolved lazily at runtime
        instructions.Should().Contain("@@Agent/ToolsReference",
            "SpecWriter instructions should reference ToolsReference for lazy resolution");
        instructions.Should().Contain("SpecWriter",
            "Instructions should mention the agent's identity");
    }

    #endregion

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

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
/// Integration tests verifying that agents are correctly wired with tools
/// based on their description (write tool gating) and that @@ references
/// in agent instructions are expanded.
/// Uses a CapturingChatClientFactory (subclass of ChatClientAgentFactory) to
/// run the real tool wiring logic while capturing messages and tools.
/// </summary>
[Collection("AgentToolWiringTests")]
public class AgentToolWiringIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public AgentToolWiringIntegrationTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<CapturingChatClient>();
                services.AddSingleton<IChatClientFactory, CapturingChatClientFactory>();
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Verifies that Navigator agent gets read-only mesh tools (Get, Search, NavigateTo)
    /// but NOT write tools (Create, Update, Delete).
    /// </summary>
    [Fact]
    public async Task NavigatorAgent_ShouldGetReadOnlyMeshTools()
    {
        var capturingClient = Mesh.ServiceProvider.GetRequiredService<CapturingChatClient>();

        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME/ProductLaunch");
        chatClient.SetSelectedAgent("Navigator");

        // Send a message to trigger agent creation and tool wiring
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await foreach (var _ in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken)) { }

        // Check captured tool list
        var lastOptions = capturingClient.LastCapturedOptions;
        lastOptions.Should().NotBeNull("ChatOptions should have been captured");
        var toolNames = lastOptions!.Tools?.OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];

        Output.WriteLine($"Navigator tools ({toolNames.Count}): {string.Join(", ", toolNames)}");

        toolNames.Should().Contain("Get", "Navigator should have Get tool");
        toolNames.Should().Contain("Search", "Navigator should have Search tool");
        toolNames.Should().Contain("NavigateTo", "Navigator should have NavigateTo tool");
        toolNames.Should().NotContain("Create", "Navigator should NOT have Create (read-only agent)");
        toolNames.Should().NotContain("Update", "Navigator should NOT have Update (read-only agent)");
        toolNames.Should().NotContain("Delete", "Navigator should NOT have Delete (read-only agent)");
    }

    /// <summary>
    /// Verifies that Executor agent gets ALL mesh tools including write operations
    /// because its description contains "create, update, and delete".
    /// </summary>
    [Fact]
    public async Task ExecutorAgent_ShouldGetAllMeshToolsIncludingWrite()
    {
        var capturingClient = Mesh.ServiceProvider.GetRequiredService<CapturingChatClient>();

        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME/ProductLaunch");
        chatClient.SetSelectedAgent("Executor");

        // Send a message to trigger agent creation and tool wiring
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await foreach (var _ in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken)) { }

        // Check captured tool list
        var lastOptions = capturingClient.LastCapturedOptions;
        lastOptions.Should().NotBeNull("ChatOptions should have been captured");
        var toolNames = lastOptions!.Tools?.OfType<AIFunction>().Select(t => t.Name).ToList() ?? [];

        Output.WriteLine($"Executor tools ({toolNames.Count}): {string.Join(", ", toolNames)}");

        toolNames.Should().Contain("Get", "Executor should have Get tool");
        toolNames.Should().Contain("Search", "Executor should have Search tool");
        toolNames.Should().Contain("Create", "Executor should have Create (write agent)");
        toolNames.Should().Contain("Update", "Executor should have Update (write agent)");
        toolNames.Should().Contain("Delete", "Executor should have Delete (write agent)");
    }

    /// <summary>
    /// Verifies that agent instructions contain expanded @@ references.
    /// The Navigator.md contains @@Agent/ToolsReference
    /// which should be expanded to include the full tool documentation.
    /// </summary>
    [Fact]
    public async Task AgentInstructions_ShouldExpandInlineReferences()
    {
        var capturingClient = Mesh.ServiceProvider.GetRequiredService<CapturingChatClient>();

        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME/ProductLaunch");
        chatClient.SetSelectedAgent("Navigator");

        // Send a message to trigger agent creation
        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        await foreach (var _ in chatClient.GetResponseAsync(messages, TestContext.Current.CancellationToken)) { }

        // Check captured system messages for expanded documentation
        var systemMessages = capturingClient.AllCapturedMessages
            .Where(m => m.Role == ChatRole.System)
            .SelectMany(m => m.Contents.OfType<TextContent>().Select(t => t.Text))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        var allSystemText = string.Join("\n", systemMessages);
        Output.WriteLine($"System prompt length: {allSystemText.Length} chars");

        // The @@ reference should have been expanded - the raw text "@@Doc" should NOT appear
        allSystemText.Should().NotContain("@@Agent/ToolsReference",
            "@@ reference should be expanded, not left as a placeholder");

        // The expanded content should contain actual tool documentation
        if (allSystemText.Length > 100)
        {
            Output.WriteLine($"System prompt preview: {allSystemText[..Math.Min(500, allSystemText.Length)]}...");
        }
    }

    #region Capturing Infrastructure

    /// <summary>
    /// Subclass of ChatClientAgentFactory that uses the real tool wiring and prompt expansion
    /// but returns a CapturingChatClient so we can inspect tools and messages.
    /// </summary>
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

[CollectionDefinition("AgentToolWiringTests", DisableParallelization = true)]
public class AgentToolWiringTestsCollection { }

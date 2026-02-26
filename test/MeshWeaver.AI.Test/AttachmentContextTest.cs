#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Integration tests verifying that SetAttachments() → BuildMessageWithContextAsync()
/// loads attachment content and places it before the user message in the assembled prompt.
/// </summary>
public class AttachmentContextTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public AttachmentContextTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new CapturingChatClientFactory());
                return services;
            })
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    #region Message-Capturing Chat Client

    /// <summary>
    /// Chat client that captures the messages it receives for assertion.
    /// The last user message contains the fully assembled prompt from BuildMessageWithContextAsync.
    /// </summary>
    /// <summary>
    /// Chat client that captures the messages it receives into a shared list.
    /// The last user message contains the fully assembled prompt from BuildMessageWithContextAsync.
    /// </summary>
    private class CapturingChatClient(List<ChatMessage> sharedCapture) : IChatClient
    {
        public ChatClientMetadata Metadata => new("CapturingProvider");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            sharedCapture.AddRange(messages);
            var reply = new ChatMessage(ChatRole.Assistant, "OK");
            return Task.FromResult(new ChatResponse(reply));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            sharedCapture.AddRange(messages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private class CapturingChatClientFactory : IChatClientFactory
    {
        /// <summary>
        /// All messages captured across all agent clients created by this factory.
        /// </summary>
        public List<ChatMessage> AllCapturedMessages { get; } = [];

        public string Name => "CapturingFactory";
        public IReadOnlyList<string> Models => ["capturing-model"];
        public int Order => 0;

        public Task<ChatClientAgent> CreateAgentAsync(
            AgentConfiguration config,
            IAgentChat chat,
            IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
            IReadOnlyList<AgentConfiguration> hierarchyAgents,
            string? modelName = null)
        {
            var client = new CapturingChatClient(AllCapturedMessages);
            var agent = new ChatClientAgent(
                chatClient: client,
                instructions: config.Instructions ?? "Test assistant.",
                name: config.Id,
                description: config.Description ?? config.Id,
                tools: [],
                loggerFactory: null,
                services: null
            );
            return Task.FromResult(agent);
        }
    }

    #endregion

    /// <summary>
    /// Helper: extracts text from captured messages, combining all TextContent items.
    /// </summary>
    private static string? GetLastUserMessageText(List<ChatMessage> messages)
    {
        return messages
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text ?? string.Join("", m.Contents.OfType<Microsoft.Extensions.AI.TextContent>().Select(t => t.Text)))
            .LastOrDefault();
    }

    /// <summary>
    /// Helper: sets up an AgentChatClient with context pointing to ACME/ProductLaunch.
    /// </summary>
    private async Task<(AgentChatClient Chat, CapturingChatClientFactory Factory)> SetupAgentChatAsync(CancellationToken ct)
    {
        var factory = (CapturingChatClientFactory)Mesh.ServiceProvider.GetRequiredService<IChatClientFactory>();

        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        await agentChat.InitializeAsync("ACME/ProductLaunch");

        var query = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        MeshNode? contextNode = null;
        await foreach (var node in query.QueryAsync<MeshNode>("path:ACME/ProductLaunch scope:self", null, ct))
        {
            contextNode = node;
            break;
        }
        contextNode.Should().NotBeNull();

        agentChat.SetContext(new AgentContext
        {
            Address = new Address("ACME", "ProductLaunch"),
            Node = contextNode
        });

        agentChat.SetThreadId($"ACME/ProductLaunch/{Guid.NewGuid().AsString()}");

        var agents = await agentChat.GetOrderedAgentsAsync();
        agents.Should().NotBeEmpty();
        agentChat.SetSelectedAgent(agents[0].Name);

        return (agentChat, factory);
    }

    /// <summary>
    /// Verifies that when attachments are set, their content appears in the assembled
    /// prompt BEFORE the user message text.
    /// </summary>
    [Fact]
    public async Task Attachments_ContentAppearsBeforeUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAgentChatAsync(ct);

        // Set an attachment to a known path in test data
        agentChat.SetAttachments(["ACME/ProductLaunch"]);

        // Send a user message
        const string userText = "What is the launch status?";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        // Get the assembled prompt from captured messages
        factory.AllCapturedMessages.Should().NotBeEmpty("the chat client should have received messages");
        var assembledPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        assembledPrompt.Should().NotBeNullOrEmpty("the assembled prompt should contain text");

        // Verify the attachment section appears before the user message text
        var attachmentIndex = assembledPrompt!.IndexOf("# Attached Content", StringComparison.Ordinal);
        var userMessageIndex = assembledPrompt.IndexOf(userText, StringComparison.Ordinal);

        attachmentIndex.Should().BeGreaterThanOrEqualTo(0, "attachment section should be present");
        userMessageIndex.Should().BeGreaterThan(0, "user message should be present");
        attachmentIndex.Should().BeLessThan(userMessageIndex,
            "attachment content should appear BEFORE the user message in the assembled prompt");
    }

    /// <summary>
    /// Verifies that attachments are cleared after use (single-use behavior).
    /// </summary>
    [Fact]
    public async Task Attachments_ClearedAfterUse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAgentChatAsync(ct);

        // First message with attachment
        agentChat.SetAttachments(["ACME/ProductLaunch"]);
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "First message")], ct)) { }

        var firstPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        firstPrompt.Should().Contain("# Attached Content", "first message should have attachments");

        // Clear captured messages for clean second check
        var messageCountAfterFirst = factory.AllCapturedMessages.Count;

        // Second message without setting attachments again (should be cleared)
        agentChat.SetThreadId($"ACME/ProductLaunch/{Guid.NewGuid().AsString()}");
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Second message")], ct)) { }

        // Get only messages from the second call
        var secondCallMessages = factory.AllCapturedMessages.Skip(messageCountAfterFirst).ToList();
        var secondPrompt = GetLastUserMessageText(secondCallMessages);
        secondPrompt.Should().NotContain("# Attached Content",
            "second message should NOT have attachments (cleared after first use)");
    }

    /// <summary>
    /// Verifies that context section also appears before the user message.
    /// </summary>
    [Fact]
    public async Task Context_AppearsBeforeUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAgentChatAsync(ct);

        const string userText = "Tell me about this";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        var assembledPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        assembledPrompt.Should().NotBeNullOrEmpty();

        var contextIndex = assembledPrompt!.IndexOf("# Current Application Context", StringComparison.Ordinal);
        var userMessageIndex = assembledPrompt.IndexOf(userText, StringComparison.Ordinal);

        contextIndex.Should().BeGreaterThanOrEqualTo(0, "context section should be present");
        userMessageIndex.Should().BeGreaterThan(0, "user message should be present");
        contextIndex.Should().BeLessThan(userMessageIndex,
            "context should appear BEFORE the user message");
    }

    /// <summary>
    /// Verifies the full ordering: agent instructions → context → attachments → user message.
    /// </summary>
    [Fact]
    public async Task PromptAssembly_FullOrdering_InstructionsContextAttachmentsUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAgentChatAsync(ct);

        agentChat.SetAttachments(["ACME/ProductLaunch"]);

        const string userText = "Check status please";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        var assembledPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        assembledPrompt.Should().NotBeNullOrEmpty();

        // Locate each section in the assembled prompt
        var instructionsIndex = assembledPrompt!.IndexOf("# Agent Identity and Instructions", StringComparison.Ordinal);
        var contextIndex = assembledPrompt.IndexOf("# Current Application Context", StringComparison.Ordinal);
        var attachmentIndex = assembledPrompt.IndexOf("# Attached Content", StringComparison.Ordinal);
        var userMessageIndex = assembledPrompt.IndexOf(userText, StringComparison.Ordinal);

        // Context must be present
        contextIndex.Should().BeGreaterThanOrEqualTo(0, "context section should be present");

        // Attachment must be present
        attachmentIndex.Should().BeGreaterThanOrEqualTo(0, "attachment section should be present");

        // User message must be present
        userMessageIndex.Should().BeGreaterThan(0, "user message should be present");

        // Verify ordering: if instructions present, they should come first
        if (instructionsIndex >= 0)
        {
            instructionsIndex.Should().BeLessThan(contextIndex,
                "agent instructions should come before context");
        }

        // Context before attachments
        contextIndex.Should().BeLessThan(attachmentIndex,
            "context should come before attachments");

        // Attachments before user message
        attachmentIndex.Should().BeLessThan(userMessageIndex,
            "attachments should come before user message");
    }

    /// <summary>
    /// Verifies that attaching an agent node does NOT inject its content into the prompt.
    /// </summary>
    [Fact]
    public async Task AgentAttachment_ExcludedFromContextContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAgentChatAsync(ct);

        // Attach an agent node as an attachment
        agentChat.SetAttachments(["Agent/Research"]);

        const string userText = "Help me find data";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        factory.AllCapturedMessages.Should().NotBeEmpty();
        var assembledPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        assembledPrompt.Should().NotBeNullOrEmpty();

        // Agent attachment should NOT appear as context content
        assembledPrompt.Should().NotContain("## Attachment: Agent/Research",
            "agent attachments should be filtered out of context content");
    }

    /// <summary>
    /// Verifies that when the main context path is an agent node, the context section is still included.
    /// </summary>
    [Fact]
    public async Task MainContext_IncludedEvenIfAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = (CapturingChatClientFactory)Mesh.ServiceProvider.GetRequiredService<IChatClientFactory>();

        var agentChat = new AgentChatClient(Mesh.ServiceProvider);
        await agentChat.InitializeAsync("Agent/Navigator");

        var query = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        MeshNode? navigatorNode = null;
        await foreach (var node in query.QueryAsync<MeshNode>("path:Agent/Navigator scope:self", null, ct))
        {
            navigatorNode = node;
            break;
        }
        navigatorNode.Should().NotBeNull();

        agentChat.SetContext(new AgentContext
        {
            Address = new Address("Agent", "Navigator"),
            Node = navigatorNode
        });

        agentChat.SetThreadId($"Agent/Navigator/{Guid.NewGuid().AsString()}");

        var agents = await agentChat.GetOrderedAgentsAsync();
        agents.Should().NotBeEmpty();
        agentChat.SetSelectedAgent(agents[0].Name);

        const string userText = "What can you do?";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        factory.AllCapturedMessages.Should().NotBeEmpty();
        var assembledPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        assembledPrompt.Should().NotBeNullOrEmpty();

        // The main context section should still be present
        assembledPrompt.Should().Contain("# Current Application Context",
            "main context should always be included, even when the context path is an agent");
    }

    /// <summary>
    /// Verifies that an @Agent/Research reference in message text overrides the combobox-selected agent.
    /// </summary>
    [Fact]
    public async Task FirstAgentReferenceInMessage_OverridesComboboxSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agentChat, factory) = await SetupAgentChatAsync(ct);

        // Explicitly select Navigator via the combobox
        agentChat.SetSelectedAgent("Navigator");

        // Send a message that references @Agent/Research (like the UI does when user types @Agent/Research)
        // Also set Agent/Research as an attachment (the UI adds @references to attachments)
        agentChat.SetAttachments(["Agent/Research"]);

        const string userText = "Please look up sales data @Agent/Research";
        await foreach (var _ in agentChat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, userText)], ct)) { }

        factory.AllCapturedMessages.Should().NotBeEmpty();
        var assembledPrompt = GetLastUserMessageText(factory.AllCapturedMessages);
        assembledPrompt.Should().NotBeNullOrEmpty();

        // The agent instructions in the prompt should be Research's, not Navigator's
        assembledPrompt.Should().Contain("You are Research",
            "the @Agent/Research reference should override the Navigator combobox selection");
        assembledPrompt.Should().NotContain("You are **Navigator**",
            "Navigator's instructions should NOT be present when Research was selected via @reference");
    }
}

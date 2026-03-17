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
/// Verifies the identity chain: CreateThread → SubmitMessage → AI streaming → response node update.
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
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new TestChatClientFactory());
                return services;
            })
            .AddMeshNodes(
                MeshNode.FromPath(UserPath) with
                {
                    Name = "Chat User",
                    NodeType = "User",
                    State = MeshNodeState.Active,
                }
            );

    protected override async Task SetupAccessRightsAsync()
    {
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        // Grant the user Editor role on their own namespace (simulates UserScopeGrantHandler)
        await securityService.AddUserRoleAsync("ChatUser", "Editor", UserPath, "system",
            TestContext.Current.CancellationToken);
    }

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
    public async Task CreateThread_WithRLS_Succeeds()
    {
        LoginAsChatUser();
        var client = GetClient();
        var ct = new CancellationTokenSource(20.Seconds()).Token;

        var response = await client.AwaitResponse(
            new CreateThreadRequest { Namespace = UserPath, UserMessageText = "Test thread" },
            o => o.WithTarget(new Address(UserPath)), ct);

        response.Message.Success.Should().BeTrue(response.Message.Error ?? "CreateThread should succeed for user with Editor role");
        response.Message.ThreadPath.Should().Contain("_Thread/");
        Output.WriteLine($"Thread created: {response.Message.ThreadPath}");
    }

    [Fact(Timeout = 30000)]
    public async Task SubmitMessage_StreamsResponse_WithRLS()
    {
        LoginAsChatUser();
        var client = GetClient();
        var ct = new CancellationTokenSource(25.Seconds()).Token;

        // 1. Create thread
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest { Namespace = UserPath, UserMessageText = "Streaming test" },
            o => o.WithTarget(new Address(UserPath)), ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;
        Output.WriteLine($"Thread: {threadPath}");

        // 2. Submit message — this triggers AI streaming on the _Exec sub-hub
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello from identity test",
                ContextPath = UserPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("Message submitted, waiting for streaming...");

        // 3. Poll for the response message to be populated by streaming
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        ThreadMessage? responseMessage = null;
        for (var i = 0; i < 50; i++)
        {
            var descendants = await meshQuery
                .QueryAsync<MeshNode>($"path:{threadPath} scope:descendants nodeType:ThreadMessage")
                .ToListAsync(ct);

            var assistantNode = descendants
                .FirstOrDefault(n => n.Content is ThreadMessage { Role: "assistant" });
            if (assistantNode?.Content is ThreadMessage tm && !string.IsNullOrEmpty(tm.Text))
            {
                responseMessage = tm;
                break;
            }
            await Task.Delay(200, ct);
        }

        responseMessage.Should().NotBeNull(
            "AI streaming should produce a response message — " +
            "if this fails, the identity chain is broken during async _Exec sub-hub execution");
        responseMessage!.Text.Should().NotBeNullOrEmpty();
        Output.WriteLine($"Response: '{responseMessage.Text}'");
    }

    [Fact(Timeout = 15000)]
    public async Task SubmitMessage_StreamsIncrementally_NotAllAtOnce()
    {
        LoginAsChatUser();
        var client = GetClient();
        var ct = new CancellationTokenSource(12.Seconds()).Token;

        // Create thread
        var createResponse = await client.AwaitResponse(
            new CreateThreadRequest { Namespace = UserPath, UserMessageText = "Incremental test" },
            o => o.WithTarget(new Address(UserPath)), ct);
        createResponse.Message.Success.Should().BeTrue(createResponse.Message.Error);
        var threadPath = createResponse.Message.ThreadPath!;

        // Submit message
        var submitTime = DateTimeOffset.UtcNow;
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Stream incrementally please",
                ContextPath = UserPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);

        // Poll for first partial response — should arrive within 5 seconds,
        // NOT after full streaming completes (which would take longer)
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        DateTimeOffset? firstResponseTime = null;
        for (var i = 0; i < 25; i++)
        {
            var descendants = await meshQuery
                .QueryAsync<MeshNode>($"path:{threadPath} scope:descendants nodeType:ThreadMessage")
                .ToListAsync(ct);

            var assistantNode = descendants
                .FirstOrDefault(n => n.Content is ThreadMessage { Role: "assistant" });
            if (assistantNode?.Content is ThreadMessage { Text.Length: > 0 })
            {
                firstResponseTime = DateTimeOffset.UtcNow;
                break;
            }
            await Task.Delay(200, ct);
        }

        firstResponseTime.Should().NotBeNull("streaming should produce partial response");
        var latency = (firstResponseTime!.Value - submitTime).TotalMilliseconds;
        Output.WriteLine($"First response appeared {latency:F0}ms after submit");

        // The first partial response should arrive within 5 seconds.
        // If updates are blocked (old bug), they'd all arrive at once after streaming completes.
        latency.Should().BeLessThan(5000,
            "first streaming update should arrive within 5s — if it takes longer, " +
            "updates are blocked in the _Exec hub's message buffer (the bug we fixed)");
    }
}

/// <summary>
/// Fake chat client for testing — returns a simple response.
/// </summary>
internal class TestChatClientFactory : IChatClientFactory
{
    private const string ResponseText = "Test response from identity-verified agent.";
    public string Name => "TestFactory";
    public IReadOnlyList<string> Models => ["test-model"];
    public int Order => 0;

    public Task<ChatClientAgent> CreateAgentAsync(
        AgentConfiguration config, IAgentChat chat,
        IReadOnlyDictionary<string, ChatClientAgent> existingAgents,
        IReadOnlyList<AgentConfiguration> hierarchyAgents,
        string? modelName = null)
    {
        return Task.FromResult(new ChatClientAgent(
            chatClient: new TestChatClient(ResponseText),
            instructions: config.Instructions ?? "Test.",
            name: config.Id, description: config.Description ?? config.Id,
            tools: [], loggerFactory: null, services: null));
    }
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

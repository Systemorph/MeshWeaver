using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// End-to-end chat test on Orleans infrastructure with FileSystem persistence.
/// Verifies CreateThreadRequest, SubmitMessageRequest, ThreadMessages streaming,
/// and GetDataRequest on Thread + ThreadMessage nodes.
/// </summary>
public class OrleansChatTest(ITestOutputHelper output) : TestBase(output)
{
    private const string ContextPath = "User/Roland";
    private TestCluster Cluster { get; set; } = null!;
    private IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<ChatSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<IMessageHub> GetClientAsync()
    {
        MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
        {
            config.TypeRegistry.AddAITypes();
            return config.AddLayoutClient();
        }

        var client = ClientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "chat"), ConfigureClient);
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var response = await client.AwaitResponse(
            new CreateThreadRequest { Namespace = ContextPath, UserMessageText = text },
            o => o.WithTarget(new Address(ContextPath)), ct);
        response.Message.Success.Should().BeTrue(response.Message.Error);
        return response.Message.ThreadPath!;
    }

    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetRemoteStream<MeshNode>(new Address(threadPath))!
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == threadPath);
                var content = node?.Content as MeshThread;
                return (IReadOnlyList<string>)(content?.ThreadMessages ?? []);
            });
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), path)),
            o => o.WithTarget(new Address(path)), ct);
        var node = response.Message.Data as MeshNode;
        if (node == null && response.Message.Data is JsonElement je)
            node = je.Deserialize<MeshNode>(ClientMesh.JsonSerializerOptions);
        if (node?.Content is T typed) return typed;
        if (node?.Content is JsonElement contentJe)
            return contentJe.Deserialize<T>(ClientMesh.JsonSerializerOptions);
        return null;
    }

    [Fact(Timeout = 60000)]
    public async Task CreateThread_AndSubmitMessage_ProducesThreadMessages()
    {
        var ct = new CancellationTokenSource(50.Seconds()).Token;
        var client = await GetClientAsync();

        // 1. Create thread
        Output.WriteLine("Creating thread...");
        var threadPath = await CreateThreadAsync(client, "Orleans chat test", ct);
        Output.WriteLine($"Thread created: {threadPath}");
        threadPath.Should().Contain("_Thread/");

        // 2. Subscribe to ThreadMessages stream
        var twoMessages = ObserveThreadMessages(client, threadPath)
            .Where(ids => ids.Count >= 2)
            .FirstAsync()
            .ToTask(ct);

        // 3. Submit message
        Output.WriteLine("Submitting message...");
        var submitResponse = await client.AwaitResponse(
            new SubmitMessageRequest
            {
                ThreadPath = threadPath,
                UserMessageText = "Hello from Orleans",
                ContextPath = ContextPath
            },
            o => o.WithTarget(new Address(threadPath)), ct);
        submitResponse.Message.Success.Should().BeTrue(submitResponse.Message.Error);
        Output.WriteLine("Message submitted");

        // 4. Wait for 2 message IDs
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user + response message IDs");
        Output.WriteLine($"ThreadMessages: [{string.Join(", ", msgIds)}]");

        // 5. Verify Thread content via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull("Thread hub should return Thread content");
        threadContent!.ThreadMessages.Should().HaveCount(2);
        Output.WriteLine($"Thread.ThreadMessages verified: {threadContent.ThreadMessages.Count}");

        // 6. Verify user message via GetDataRequest
        var userContent = await GetHubContentAsync<ThreadMessage>(
            client, $"{threadPath}/{msgIds[0]}", ct);
        userContent.Should().NotBeNull("user message hub should return ThreadMessage");
        userContent!.Role.Should().Be("user");
        userContent.Text.Should().Be("Hello from Orleans");
        Output.WriteLine($"User message verified: '{userContent.Text}'");

        // 7. Verify response message — poll until streaming completes
        ThreadMessage? responseContent = null;
        var prevLen = 0;
        var stable = 0;
        for (var i = 0; i < 50; i++)
        {
            responseContent = await GetHubContentAsync<ThreadMessage>(
                client, $"{threadPath}/{msgIds[1]}", ct);
            var len = responseContent?.Text?.Length ?? 0;
            if (len > 0 && len == prevLen && ++stable >= 2) break;
            else stable = 0;
            prevLen = len;
            await Task.Delay(200, ct);
        }

        responseContent.Should().NotBeNull("response message hub should return ThreadMessage");
        responseContent!.Role.Should().Be("assistant");
        responseContent.Text.Should().NotBeNullOrEmpty("streaming should produce non-empty response");
        Output.WriteLine($"Response verified: '{responseContent.Text}' ({responseContent.Text.Length} chars)");
    }
}

/// <summary>
/// Silo configurator for chat tests: FileSystem persistence + AddGraph + AddAI + FakeChatClient.
/// </summary>
public class ChatSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddFileSystemPersistence(SamplesGraphData)
            .ConfigurePortalMesh()
            .AddGraph()
            .AddAI()
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}

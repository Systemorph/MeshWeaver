using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

// TODO: needs custom shared fixture â€” uses ChatSiloConfigurator with AddFileSystemPersistence(SamplesGraphData),
// which the SharedOrleansFixture does not configure.
/// <summary>
/// End-to-end chat test on Orleans infrastructure with FileSystem persistence.
/// Verifies CreateNodeRequest, ThreadInput.AppendUserInput, ThreadMessages streaming,
/// and GetDataRequest on Thread + ThreadMessage nodes.
/// </summary>
public class OrleansChatTest(ITestOutputHelper output) : OrleansTestBase<ChatSiloConfigurator>(output)
{
    private const string ContextPath = "TestUser";

    // Cluster lifecycle, ClientMesh, GetClient, ConfigureClient, and the standard
    // mesh-node handler chain are inherited from OrleansTestBase<TSiloConfigurator>.

    private async Task<string> CreateThreadAsync(IMessageHub client, string text, CancellationToken ct)
    {
        var response = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(ContextPath, text)), o => o.WithTarget(new Address(ContextPath))).FirstAsync().ToTask(ct);
        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        return response.Message.Node!.Path!;
    }

    private IObservable<IReadOnlyList<string>> ObserveThreadMessages(IMessageHub client, string threadPath)
    {
        var workspace = client.GetWorkspace();
        return workspace.GetMeshNodeStream(threadPath)
            .Select(node =>
            {
                
                var content = node?.Content as MeshThread;
                return (IReadOnlyList<string>)(content?.Messages ?? []);
            });
    }

    private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct) where T : class
    {
        // Canonical CQRS-correct read: target the per-node hub's MeshNodeReference
        // reducer, not an EntityCollection lookup. The owning hub is the source of
        // truth for MeshNode content; this avoids any catalog / index lag.
        var response = await client.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path))).FirstAsync().ToTask(ct);
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
        var client = GetClient();

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

        // 3. Submit message via ThreadInput.AppendUserInput — see RequestViaStreamUpdate.md.
        Output.WriteLine("Submitting message...");
        client.SubmitMessage(
            threadPath,
            "Hello from Orleans",
            contextPath: ContextPath);
        Output.WriteLine("Message submitted");

        // 4. Wait for 2 message IDs
        var msgIds = await twoMessages;
        msgIds.Should().HaveCount(2, "should have user + response message IDs");
        Output.WriteLine($"ThreadMessages: [{string.Join(", ", msgIds)}]");

        // 5. Verify Thread content via GetDataRequest
        var threadContent = await GetHubContentAsync<MeshThread>(client, threadPath, ct);
        threadContent.Should().NotBeNull("Thread hub should return Thread content");
        threadContent!.Messages.Should().HaveCount(2);
        Output.WriteLine($"Thread.Messages verified: {threadContent.Messages.Count}");

        // 6. Verify user message via GetDataRequest
        var userContent = await GetHubContentAsync<ThreadMessage>(
            client, $"{threadPath}/{msgIds[0]}", ct);
        userContent.Should().NotBeNull("user message hub should return ThreadMessage");
        userContent!.Role.Should().Be("user");
        userContent.Text.Should().Be("Hello from Orleans");
        Output.WriteLine($"User message verified: '{userContent.Text}'");

        // 7. Verify the response cell — wait for its TERMINAL status.
        //
        //    🚨 This used to be a hand-rolled `for (50) { read; if (text length unchanged
        //    twice) break; await Task.Delay(200) }` loop. Three things wrong with it, and
        //    together they made this one of #1384's recurring unrelated CI reds (three
        //    failures across three unrelated branches, 08-10 → 08-12):
        //      • "the text stopped growing for 400 ms" is a SLEEP-based guess at "streaming
        //        finished". ThreadExecution pushes through Sample(100 ms), so any scheduling
        //        hiccup on a loaded runner produces two equal-length reads mid-stream — the
        //        loop breaks early — or never two in a row, and it runs the full 50 rounds.
        //      • 50 iterations × (a GetDataRequest round-trip + 200 ms) blows past the
        //        test's own 50 s CancellationToken, which surfaces as a bare
        //        `TaskCanceledException` from the helper rather than a useful assertion.
        //      • Hand-rolled `while + Task.Delay` poll loops are forbidden outright
        //        (AGENTS.md → Testing Guidelines).
        //    The cell is created at Streaming and reaches Completed exactly when the
        //    streaming loop exits, so the terminal status IS the condition — no heuristic,
        //    no sleep, and it cannot pass before the round is genuinely done.
        var responseContent = await client.GetWorkspace()
            .GetMeshNodeStream($"{threadPath}/{msgIds[1]}")
            .Select(node => node.ContentAs<ThreadMessage>(client.JsonSerializerOptions))
            .Should().Within(40.Seconds())
            .Match(m => m is { Status: ThreadMessageStatus.Completed });

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
                services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory());
                services.AddSingleton<IStaticNodeProvider, OrleansTestSeedProvider>();
                return services;
            })
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}

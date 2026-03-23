using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshThread = MeshWeaver.AI.Thread;

namespace MeshWeaver.Threading.Test;

/// <summary>
/// Tests that MeshNodeReference works as a single-instance stream (not collection).
/// Updates via UpdateMeshNode should produce clean JSON patches on the MeshNode directly,
/// without InstanceCollection key wrapping or path escaping issues.
/// </summary>
public class MeshNodeReferenceSingleInstanceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task GetRemoteStream_CollectionReference_ReturnsMeshNode()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create a thread node
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "single instance test", "Roland");
        var created = await NodeFactory.CreateNodeAsync(threadNode, ct);
        var threadPath = created.Path;

        // Get via CollectionReference for MeshNode collection
        var client = GetClient();
        var stream = client.GetWorkspace()
            .GetRemoteStream<InstanceCollection, CollectionReference>(
                new Address(threadPath), new CollectionReference(nameof(MeshNode)));

        stream.Should().NotBeNull();

        var node = await stream!
            .Select(ci => ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault())
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null);

        node.Should().NotBeNull();
        node!.Path.Should().Be(threadPath);
        node.Content.Should().BeOfType<MeshThread>();
        Output.WriteLine($"Got single node: {node.Path}");
    }

    [Fact]
    public async Task UpdateMeshNode_SingleUpdate_MessagesChange()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "update test", "Roland");
        var created = await NodeFactory.CreateNodeAsync(threadNode, ct);
        var threadPath = created.Path;

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Subscribe to the MeshNode collection stream
        var collectionStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            new Address(threadPath), new CollectionReference(nameof(MeshNode)));

        // Wait for initial node
        var initial = await collectionStream!
            .Select(ci => ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault())
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null);
        var initialContent = initial!.Content as MeshThread;
        initialContent!.Messages.Should().BeEmpty();

        // Update Messages to ["msg1"]
        var updated = collectionStream
            .Select(ci =>
            {
                var node = ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault();
                var t = node?.Content as MeshThread;
                Output.WriteLine($"[STREAM] ChangeType={ci.ChangeType}, Value={node?.Id}, Messages={t?.Messages.Count}");
                return node;
            })
            .Timeout(5.Seconds())
            .FirstAsync(n =>
            {
                var t = n?.Content as MeshThread;
                return t?.Messages.Count >= 1;
            }).ToTask(ct);

        Output.WriteLine($"[TEST] Calling UpdateMeshNode for {threadPath}");
        workspace.UpdateMeshNode(node =>
        {
            if (node?.Content is null)
                throw new InvalidOperationException("Node content is null");
            Output.WriteLine($"[TEST] UpdateMeshNode callback: node={node.Id}, Content={node.Content.GetType().Name}");
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = ImmutableList.Create("msg1") } };
        }, new Address(threadPath), threadPath);
        Output.WriteLine("[TEST] UpdateMeshNode called, waiting for stream emission");

        var result = await updated;
        result.Should().NotBeNull();
        result!.Path.Should().Be(threadPath);
        var resultContent = result.Content as MeshThread;
        resultContent.Should().NotBeNull();
        resultContent!.Messages.Should().BeEquivalentTo(new[] { "msg1" });
        Output.WriteLine($"After update 1: Messages=[{string.Join(", ", resultContent.Messages)}]");

        // Verify back-sync: GetDataRequest on the thread hub should return updated content
        var nodeId = threadPath.Contains('/') ? threadPath[(threadPath.LastIndexOf('/') + 1)..] : threadPath;
        var response = await client.AwaitResponse(
            new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
            o => o.WithTarget(new Address(threadPath)), ct);
        var serverNode = response.Message.Data as MeshNode;
        if (serverNode == null && response.Message.Data is System.Text.Json.JsonElement je)
            serverNode = je.Deserialize<MeshNode>(Mesh.JsonSerializerOptions);
        serverNode.Should().NotBeNull("server should return the MeshNode via GetDataRequest");
        var serverContent = serverNode!.Content as MeshThread;
        serverContent.Should().NotBeNull("server MeshNode should have Thread content");
        serverContent!.Messages.Should().BeEquivalentTo(new[] { "msg1" },
            "server should reflect the client's update (back-sync)");
        Output.WriteLine($"Back-sync verified: server Messages=[{string.Join(", ", serverContent.Messages)}]");
    }

    [Fact]
    public async Task UpdateMeshNode_MultipleUpdates_AccumulateMessages()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "multi update test", "Roland");
        var created = await NodeFactory.CreateNodeAsync(threadNode, ct);
        var threadPath = created.Path;

        var client = GetClient();
        var workspace = client.GetWorkspace();

        var collectionStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            new Address(threadPath), new CollectionReference(nameof(MeshNode)));

        MeshNode? ExtractNode(ChangeItem<InstanceCollection> ci) =>
            ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault();

        // Wait for initial
        await collectionStream!
            .Select(ExtractNode)
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null);

        // Update 1: add msg1
        var afterFirst = collectionStream
            .Select(ExtractNode)
            .Timeout(5.Seconds())
            .FirstAsync(n => (n?.Content as MeshThread)?.Messages.Count >= 1)
            .ToTask(ct);

        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = thread.Messages.Add("msg1") } };
        }, new Address(threadPath), threadPath);

        var r1 = await afterFirst;
        Output.WriteLine($"After update 1: Messages=[{string.Join(", ", ((MeshThread)r1!.Content!).Messages)}]");

        // Update 2: add msg2
        var afterSecond = collectionStream
            .Select(ExtractNode)
            .Timeout(5.Seconds())
            .FirstAsync(n => (n?.Content as MeshThread)?.Messages.Count >= 2)
            .ToTask(ct);

        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = thread.Messages.Add("msg2") } };
        }, new Address(threadPath), threadPath);

        var r2 = await afterSecond;
        var finalContent = r2!.Content as MeshThread;
        finalContent!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2" });
        Output.WriteLine($"After update 2: Messages=[{string.Join(", ", finalContent.Messages)}]");

        // Update 3: add msg3
        var afterThird = collectionStream
            .Select(ExtractNode)
            .Timeout(5.Seconds())
            .FirstAsync(n => (n?.Content as MeshThread)?.Messages.Count >= 3)
            .ToTask(ct);

        workspace.UpdateMeshNode(node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = thread.Messages.Add("msg3") } };
        }, new Address(threadPath), threadPath);

        var r3 = await afterThird;
        var final3 = r3!.Content as MeshThread;
        final3!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2", "msg3" });
        Output.WriteLine($"After update 3: Messages=[{string.Join(", ", final3.Messages)}]");
    }

    /// <summary>
    /// Tests the ThreadsCatalog view injected via AddAI → AddThreadType → AddThreadLayoutAreas.
    /// Verifies that from a thread's Threads area we can create a new sub-thread (delegation).
    /// </summary>
    [Fact]
    public async Task ThreadsCatalog_CreateNewThread_Succeeds()
    {
        var ct = new CancellationTokenSource(15.Seconds()).Token;

        // 1. Create a context node and a thread under it
        var contextPath = "TestContext";
        await NodeFactory.CreateNodeAsync(
            new MeshNode(contextPath) { Name = "Test Context", NodeType = "Markdown" }, ct);

        var client = GetClient();
        var response = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Parent thread for catalog test")),
            o => o.WithTarget(new Address(contextPath)),
            ct);

        response.Message.Success.Should().BeTrue(response.Message.Error);
        var threadPath = response.Message.Node!.Path;
        Output.WriteLine($"Created parent thread at: {threadPath}");

        // 2. Subscribe to the ThreadsArea on the thread hub — this renders ThreadsCatalog
        var workspace = client.GetWorkspace();
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(MeshNodeLayoutAreas.ThreadsArea));
        layoutStream.Should().NotBeNull("Thread hub should serve the Threads area (ThreadsCatalog)");

        // Wait for the layout to render
        var layout = await layoutStream!.Timeout(10.Seconds()).FirstAsync();
        Output.WriteLine("ThreadsCatalog layout rendered");

        // 3. Create a sub-thread (delegation) via CreateNodeRequest — same flow as the "Create Thread" button
        var subResponse = await client.AwaitResponse(
            new CreateNodeRequest(ThreadNodeType.BuildThreadNode(threadPath, "Delegation sub-thread")),
            o => o.WithTarget(new Address(threadPath)),
            ct);

        subResponse.Message.Success.Should().BeTrue(subResponse.Message.Error);
        var subThreadPath = subResponse.Message.Node!.Path;
        Output.WriteLine($"Created sub-thread at: {subThreadPath}");

        subThreadPath.Should().StartWith($"{threadPath}/");
        subThreadPath.Should().Contain($"/{ThreadNodeType.ThreadPartition}/");

        // 4. Verify the sub-thread is queryable with the same query ThreadsCatalog uses
        //    ThreadsCatalog queries: namespace:{hubPath}/_Thread nodeType:Thread
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var threads = await meshQuery.QueryAsync<MeshNode>(
            $"namespace:{threadPath}/{ThreadNodeType.ThreadPartition} nodeType:{ThreadNodeType.NodeType}").ToListAsync(ct);

        threads.Should().ContainSingle("should find the created sub-thread");
        threads[0].Path.Should().Be(subThreadPath);
        threads[0].Content.Should().BeOfType<MeshThread>();
        var content = (MeshThread)threads[0].Content!;
        content.ParentPath.Should().Be(threadPath);
        Output.WriteLine($"Sub-thread verified: {threads[0].Path}, ParentPath={content.ParentPath}");
    }
}

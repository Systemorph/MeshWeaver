using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        // Create a thread node
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "single instance test", "Roland");
        var created = await NodeFactory.CreateNode(threadNode).Should().Emit();
        var threadPath = created.Path;

        // Get via CollectionReference for MeshNode collection
        var client = GetClient();
        var stream = client.GetWorkspace()
            .GetRemoteStream<InstanceCollection, CollectionReference>(
                new Address(threadPath), new CollectionReference(nameof(MeshNode)));

        var node = await stream
            .Select(ci => ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault())
            .Should().Within(5.Seconds()).Match(n => n != null);

        node!.Path.Should().Be(threadPath);
        node.Content.Should().BeOfType<MeshThread>();
        Output.WriteLine($"Got single node: {node.Path}");
    }

    [Fact]
    public async Task UpdateMeshNode_SingleUpdate_MessagesChange()
    {
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "update test", "Roland");
        var created = await NodeFactory.CreateNode(threadNode).Should().Emit();
        var threadPath = created.Path;

        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Subscribe to the MeshNode collection stream
        var collectionStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            new Address(threadPath), new CollectionReference(nameof(MeshNode)));

        // Wait for initial node
        var initial = await collectionStream
            .Select(ci => ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault())
            .Should().Within(5.Seconds()).Match(n => n != null);
        var initialContent = initial!.Content as MeshThread;
        initialContent!.Messages.Should().BeEmpty();

        // Cross-hub update via DataChangeRequest — read current via the
        // collectionStream subscription, build the patch, post.
        Output.WriteLine($"[TEST] Posting DataChangeRequest for {threadPath}");
        {
            var current = collectionStream.Current?.Value?.Instances.Values
                .OfType<MeshNode>().FirstOrDefault();
            if (current is null)
                throw new InvalidOperationException("Node not found in collection stream");
            Output.WriteLine($"[TEST] DataChangeRequest: node={current.Id}, Content={current.Content?.GetType().Name}");
            var thread = current.Content as MeshThread ?? new MeshThread();
            var patched = current with { Content = thread with { Messages = ImmutableList.Create("msg1") } };
            client.Post(
                new DataChangeRequest { Updates = [patched] },
                o => o.WithTarget(new Address(threadPath)));
        }
        Output.WriteLine("[TEST] DataChangeRequest posted, waiting for stream emission");

        // Update Messages to ["msg1"]
        var result = await collectionStream
            .Select(ci =>
            {
                var node = ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault();
                var t = node?.Content as MeshThread;
                Output.WriteLine($"[STREAM] ChangeType={ci.ChangeType}, Value={node?.Id}, Messages={t?.Messages.Count}");
                return node;
            })
            .Should().Within(5.Seconds()).Match(n => (n?.Content as MeshThread)?.Messages.Count >= 1);

        result!.Path.Should().Be(threadPath);
        var resultContent = result.Content as MeshThread;
        resultContent.Should().NotBeNull();
        resultContent!.Messages.Should().BeEquivalentTo(new[] { "msg1" }, client.JsonSerializerOptions);
        Output.WriteLine($"After update 1: Messages=[{string.Join(", ", resultContent.Messages)}]");

        // Verify back-sync via the canonical MeshNode stream handle — same
        // primitive the GUI uses; no GetDataRequest polling.
        var serverNode = await workspace.GetMeshNodeStream(threadPath)
            .Should().Within(5.Seconds()).Match(n => (n.Content as MeshThread)?.Messages.Count >= 1);
        var serverContent = serverNode.Content as MeshThread;
        serverContent.Should().NotBeNull("server MeshNode should have Thread content");
        serverContent!.Messages.Should().BeEquivalentTo(new[] { "msg1" }, client.JsonSerializerOptions,
            because: "server should reflect the client's update (back-sync)");
        Output.WriteLine($"Back-sync verified: server Messages=[{string.Join(", ", serverContent.Messages)}]");
    }

    [Fact]
    public async Task UpdateMeshNode_MultipleUpdates_AccumulateMessages()
    {
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "multi update test", "Roland");
        var created = await NodeFactory.CreateNode(threadNode).Should().Emit();
        var threadPath = created.Path;

        var client = GetClient();
        var workspace = client.GetWorkspace();

        var collectionStream = workspace.GetRemoteStream<InstanceCollection, CollectionReference>(
            new Address(threadPath), new CollectionReference(nameof(MeshNode)));

        MeshNode? ExtractNode(ChangeItem<InstanceCollection> ci) =>
            ci.Value?.Instances.Values.OfType<MeshNode>().FirstOrDefault();

        IObservable<MeshNode?> nodes = collectionStream.Select(ExtractNode);

        // Wait for initial
        await nodes.Should().Within(5.Seconds()).Match(n => n != null);

        // Update 1: add msg1 — read current via the collectionStream subscription
        // above, build the patch, post.
        var current = collectionStream.Current?.Value?.Instances.Values
            .OfType<MeshNode>().FirstOrDefault();
        if (current is { Content: MeshThread t1 })
        {
            client.Post(
                new DataChangeRequest { Updates = [current with { Content = t1 with { Messages = t1.Messages.Add("msg1") } }] },
                o => o.WithTarget(new Address(threadPath)));
        }

        var r1 = await nodes.Should().Within(5.Seconds())
            .Match(n => (n?.Content as MeshThread)?.Messages.Count >= 1);
        Output.WriteLine($"After update 1: Messages=[{string.Join(", ", ((MeshThread)r1!.Content!).Messages)}]");

        // Update 2: add msg2
        {
            var c = collectionStream.Current?.Value?.Instances.Values
                .OfType<MeshNode>().FirstOrDefault();
            if (c is { Content: MeshThread t2 })
            {
                client.Post(
                    new DataChangeRequest { Updates = [c with { Content = t2 with { Messages = t2.Messages.Add("msg2") } }] },
                    o => o.WithTarget(new Address(threadPath)));
            }
        }

        var r2 = await nodes.Should().Within(5.Seconds())
            .Match(n => (n?.Content as MeshThread)?.Messages.Count >= 2);
        var finalContent = r2!.Content as MeshThread;
        finalContent!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2" }, client.JsonSerializerOptions);
        Output.WriteLine($"After update 2: Messages=[{string.Join(", ", finalContent.Messages)}]");

        // Update 3: add msg3
        {
            var c = collectionStream.Current?.Value?.Instances.Values
                .OfType<MeshNode>().FirstOrDefault();
            if (c is { Content: MeshThread t3 })
            {
                client.Post(
                    new DataChangeRequest { Updates = [c with { Content = t3 with { Messages = t3.Messages.Add("msg3") } }] },
                    o => o.WithTarget(new Address(threadPath)));
            }
        }

        var r3 = await nodes.Should().Within(5.Seconds())
            .Match(n => (n?.Content as MeshThread)?.Messages.Count >= 3);
        var final3 = r3!.Content as MeshThread;
        final3!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2", "msg3" }, client.JsonSerializerOptions);
        Output.WriteLine($"After update 3: Messages=[{string.Join(", ", final3.Messages)}]");
    }

    /// <summary>
    /// Tests the ThreadsCatalog view injected via AddAI → AddThreadType → AddThreadLayoutAreas.
    /// Verifies that from a thread's Threads area we can create a new sub-thread (delegation).
    /// </summary>
    [Fact]
    public async Task ThreadsCatalog_CreateNewThread_Succeeds()
    {
        // 1. Create a context node and a thread under it
        var contextPath = "TestContext";
        // Top-level partition root → seed under System (only the partition provisioner may create there).
        await SeedTopLevel(new MeshNode(contextPath) { Name = "Test Context", NodeType = "Markdown" });

        var client = GetClient();
        var response = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(contextPath, "Parent thread for catalog test")), o => o.WithTarget(new Address(contextPath))).Should().Emit();

        response.Message.Success.Should().BeTrue(response.Message.Error ?? "");
        var threadPath = response.Message.Node!.Path;
        Output.WriteLine($"Created parent thread at: {threadPath}");

        // 2. Subscribe to the ThreadsArea on the thread hub — this renders ThreadsCatalog
        var workspace = client.GetWorkspace();
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(threadPath),
            new LayoutAreaReference(MeshNodeLayoutAreas.ThreadsArea));

        // Wait for the layout to render — the hub must serve the Threads area (ThreadsCatalog)
        await layoutStream.Should().Within(10.Seconds()).Emit();
        Output.WriteLine("ThreadsCatalog layout rendered");

        // 3. Create a sub-thread (delegation) via CreateNodeRequest — same flow as the "Create Thread" button
        var subResponse = await client.Observe(new CreateNodeRequest(ThreadNodeType.BuildThreadNode(threadPath, "Delegation sub-thread")), o => o.WithTarget(new Address(threadPath))).Should().Emit();

        subResponse.Message.Success.Should().BeTrue(subResponse.Message.Error ?? "");
        var subThreadPath = subResponse.Message.Node!.Path;
        Output.WriteLine($"Created sub-thread at: {subThreadPath}");

        subThreadPath.Should().StartWith($"{threadPath}/");
        // Sub-threads under a parent that's already inside _Thread don't get
        // another _Thread partition — BuildThreadNode skips nested _Thread.

        // 4. Verify the sub-thread is queryable — lives directly under the parent thread namespace
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var threads = (await meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
            $"namespace:{threadPath} nodeType:{ThreadNodeType.NodeType}"))
            .Should().Match(c => c.Items.Count >= 1)).Items;

        threads.Should().ContainSingle("should find the created sub-thread");
        threads[0].Path.Should().Be(subThreadPath);
        threads[0].Content.Should().BeOfType<MeshThread>();
        Output.WriteLine($"Sub-thread verified: {threads[0].Path}");
    }
}

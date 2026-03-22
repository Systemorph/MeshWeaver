using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
    public async Task GetRemoteStream_MeshNodeReference_ReturnsSingleNode()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // Create a thread node
        var threadNode = ThreadNodeType.BuildThreadNode("User/Roland", "single instance test", "Roland");
        var created = await NodeFactory.CreateNodeAsync(threadNode, ct);
        var threadPath = created.Path;

        // Get via MeshNodeReference — should be a single MeshNode, not a collection
        var client = GetClient();
        var stream = client.GetWorkspace()
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(threadPath), new MeshNodeReference());

        stream.Should().NotBeNull();

        var node = await stream!
            .Select(ci => ci.Value)
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

        // Subscribe to the MeshNode stream
        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(threadPath), new MeshNodeReference());

        // Wait for initial node
        var initial = await stream!
            .Select(ci => ci.Value)
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null);
        var initialContent = initial!.Content as MeshThread;
        initialContent!.Messages.Should().BeEmpty();

        // Update Messages to ["msg1"]
        var updated = stream
            .Select(ci =>
            {
                var t = ci.Value?.Content as MeshThread;
                Output.WriteLine($"[STREAM] ChangeType={ci.ChangeType}, Value={ci.Value?.Id}, Messages={t?.Messages.Count}");
                return ci.Value;
            })
            .Timeout(5.Seconds())
            .FirstAsync(n =>
            {
                var t = n?.Content as MeshThread;
                return t?.Messages.Count >= 1;
            }).ToTask(ct);

        Output.WriteLine($"[TEST] Calling UpdateMeshNode for {threadPath}");
        workspace.UpdateMeshNode(new Address(threadPath), threadPath, node =>
        {
            Output.WriteLine($"[TEST] UpdateMeshNode callback: node={node?.Id}, Content={node?.Content?.GetType().Name}");
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = ImmutableList.Create("msg1") } };
        });
        Output.WriteLine("[TEST] UpdateMeshNode called, waiting for stream emission");

        var result = await updated;
        result.Should().NotBeNull();
        result!.Path.Should().Be(threadPath);
        var resultContent = result.Content as MeshThread;
        resultContent.Should().NotBeNull();
        resultContent!.Messages.Should().BeEquivalentTo(new[] { "msg1" });
        Output.WriteLine($"After update 1: Messages=[{string.Join(", ", resultContent.Messages)}]");
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

        var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(threadPath), new MeshNodeReference());

        // Wait for initial
        await stream!
            .Select(ci => ci.Value)
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null);

        // Update 1: add msg1
        var afterFirst = stream
            .Select(ci => ci.Value)
            .Timeout(5.Seconds())
            .FirstAsync(n => (n?.Content as MeshThread)?.Messages.Count >= 1)
            .ToTask(ct);

        workspace.UpdateMeshNode(new Address(threadPath), threadPath, node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = thread.Messages.Add("msg1") } };
        });

        var r1 = await afterFirst;
        Output.WriteLine($"After update 1: Messages=[{string.Join(", ", ((MeshThread)r1!.Content!).Messages)}]");

        // Update 2: add msg2
        var afterSecond = stream
            .Select(ci => ci.Value)
            .Timeout(5.Seconds())
            .FirstAsync(n => (n?.Content as MeshThread)?.Messages.Count >= 2)
            .ToTask(ct);

        workspace.UpdateMeshNode(new Address(threadPath), threadPath, node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = thread.Messages.Add("msg2") } };
        });

        var r2 = await afterSecond;
        var finalContent = r2!.Content as MeshThread;
        finalContent!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2" });
        Output.WriteLine($"After update 2: Messages=[{string.Join(", ", finalContent.Messages)}]");

        // Update 3: add msg3
        var afterThird = stream
            .Select(ci => ci.Value)
            .Timeout(5.Seconds())
            .FirstAsync(n => (n?.Content as MeshThread)?.Messages.Count >= 3)
            .ToTask(ct);

        workspace.UpdateMeshNode(new Address(threadPath), threadPath, node =>
        {
            var thread = node.Content as MeshThread ?? new MeshThread();
            return node with { Content = thread with { Messages = thread.Messages.Add("msg3") } };
        });

        var r3 = await afterThird;
        var final3 = r3!.Content as MeshThread;
        final3!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2", "msg3" });
        Output.WriteLine($"After update 3: Messages=[{string.Join(", ", final3.Messages)}]");
    }
}

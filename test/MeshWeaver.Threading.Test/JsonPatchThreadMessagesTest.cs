using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
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
/// Tests that patching Thread.Messages via the workspace stream produces
/// a valid MeshNode (not a bare Thread object).
/// Reproduces the bug where NavigateToParent strips the InstanceCollection
/// key wrapper during JSON patch.
/// </summary>
public class JsonPatchThreadMessagesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddAI()
            .AddSampleUsers();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    [Fact]
    public async Task CreateThread_ThenUpdateMessages_ProducesValidMeshNode()
    {
        var ct = new CancellationTokenSource(10.Seconds()).Token;

        // 1. Create thread
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "patch test", "Roland");
        var created = await NodeFactory.CreateNodeAsync(threadNode, ct);
        var threadPath = created.Path;
        Output.WriteLine($"Thread created: {threadPath}");

        // 2. Get the stream from client
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var remoteStream = workspace.GetRemoteStream<MeshNode>(new Address(threadPath));
        remoteStream.Should().NotBeNull();

        // 3. Read initial node — should be valid with empty messages
        var initialNodes = await remoteStream!
            .Timeout(5.Seconds())
            .FirstAsync(n => n != null && n.Any());
        var initialNode = initialNodes.FirstOrDefault(n => n.Path == threadPath);
        initialNode.Should().NotBeNull();
        var initialContent = initialNode!.Content as MeshThread;
        initialContent.Should().NotBeNull();
        initialContent!.Messages.Should().BeEmpty();
        Output.WriteLine($"Initial node OK: {initialNode.Id}, Messages={initialContent.Messages.Count}");

        // 4. Update Messages via DataChangeRequest (like SubmitMessage does)
        var updatedNode = initialNode with
        {
            Content = initialContent with
            {
                Messages = ImmutableList.Create("msg1", "msg2")
            }
        };
        client.Post(new DataChangeRequest { Updates = [updatedNode] },
            o => o.WithTarget(new Address(threadPath)));

        // 5. Wait for the updated node — should still be a valid MeshNode
        var updatedNodes = await remoteStream
            .Timeout(5.Seconds())
            .FirstAsync(n =>
            {
                var node = n?.FirstOrDefault(x => x.Path == threadPath);
                var content = node?.Content as MeshThread;
                return content?.Messages.Count >= 2;
            });

        var resultNode = updatedNodes.FirstOrDefault(n => n.Path == threadPath);
        resultNode.Should().NotBeNull("patched result should be a valid MeshNode");
        resultNode!.Id.Should().Be(initialNode.Id, "Id should be preserved after patch");
        resultNode.NodeType.Should().Be("Thread", "NodeType should be preserved");

        var resultContent = resultNode.Content as MeshThread;
        resultContent.Should().NotBeNull("Content should still be a Thread");
        resultContent!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2" });
        Output.WriteLine($"Patched node OK: {resultNode.Id}, Messages=[{string.Join(", ", resultContent.Messages)}]");
    }
}

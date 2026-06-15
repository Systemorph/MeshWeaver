using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
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
/// Patching <c>Thread.Messages</c> via <see cref="DataChangeRequest"/> must produce
/// a valid MeshNode (not a bare Thread object). Reads use the canonical
/// <c>workspace.GetMeshNodeStream(path)</c> live handle.
/// </summary>
public class JsonPatchThreadMessagesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string ContextPath = "User/Roland";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddAI().AddSampleUsers();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        configuration.TypeRegistry.AddAITypes();
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    [Fact]
    public async Task CreateThread_ThenUpdateMessages_ProducesValidMeshNode()
    {
        var threadNode = ThreadNodeType.BuildThreadNode(ContextPath, "patch test", "Roland");
        var created = await NodeFactory.CreateNode(threadNode).Should().Emit();
        var threadPath = created.Path;
        Output.WriteLine($"Thread created: {threadPath}");

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var threadStream = workspace.GetMeshNodeStream(threadPath);

        var initial = await threadStream.Should().Within(5.Seconds()).Emit();
        var initialContent = initial.Content as MeshThread;
        initialContent.Should().NotBeNull();
        initialContent!.Messages.Should().BeEmpty();
        Output.WriteLine($"Initial node OK: {initial.Id}, Messages={initialContent.Messages.Count}");

        var updatedNode = initial with
        {
            Content = initialContent with { Messages = ImmutableList.Create("msg1", "msg2") }
        };
        client.Post(new DataChangeRequest { Updates = [updatedNode] },
            o => o.WithTarget(new Address(threadPath)));

        var resultNode = await threadStream.Should().Within(5.Seconds())
            .Match(n => (n.Content as MeshThread)?.Messages.Count >= 2);

        resultNode.Id.Should().Be(initial.Id, "Id should be preserved after patch");
        resultNode.NodeType.Should().Be("Thread", "NodeType should be preserved");

        var resultContent = resultNode.Content as MeshThread;
        resultContent.Should().NotBeNull();
        resultContent!.Messages.Should().BeEquivalentTo(new[] { "msg1", "msg2" }, client.JsonSerializerOptions);
        Output.WriteLine($"Patched node OK: {resultNode.Id}, Messages=[{string.Join(", ", resultContent.Messages)}]");
    }
}

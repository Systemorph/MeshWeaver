using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

public class NodeCopyHelperTest(ITestOutputHelper output) : HubTestBase(output)
{
    private InMemoryPersistenceService _persistence = null!;
    private static readonly JsonSerializerOptions JsonOptions = new();

    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
    {
        _persistence = new InMemoryPersistenceService();

        return conf
            .WithServices(services => services
                .AddSingleton<IPersistenceServiceCore>(_persistence)
                .AddSingleton<IPersistenceService>(sp =>
                    new PersistenceService(sp.GetRequiredService<IPersistenceServiceCore>(),
                        sp.GetRequiredService<IMessageHub>())))
            .WithRoutes(forward => forward
                .RouteAddressToHostedHub(HostType, ConfigureHost)
                .RouteAddressToHostedHub(ClientType, ConfigureClient));
    }

    private IPersistenceService GetPersistenceService()
        => GetHost().ServiceProvider.GetRequiredService<IPersistenceService>();

    private async Task SaveNode(string path, string? name = null, string? nodeType = null, object? content = null)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = name ?? path.Split('/').Last(),
            NodeType = nodeType ?? "Markdown",
            Content = content,
            State = MeshNodeState.Active
        };
        await _persistence.SaveNodeAsync(node, JsonOptions);
    }

    [HubFact]
    public async Task CopySingleNode_ToNewNamespace()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "org/Acme", "workspace", force: false);

        copied.Should().Be(1);

        var target = await persistence.GetNodeAsync("workspace/Acme");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Acme Corp");
        target.NodeType.Should().Be("Organization");
        target.State.Should().Be(MeshNodeState.Active);
    }

    [HubFact]
    public async Task CopyNodeTree_WithDescendants()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        await SaveNode("org/Acme/Team1", "Team One", "Team");
        await SaveNode("org/Acme/Team2", "Team Two", "Team");
        await SaveNode("org/Acme/Team1/Alice", "Alice", "Person");
        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "org/Acme", "workspace", force: false);

        copied.Should().Be(4);

        (await persistence.GetNodeAsync("workspace/Acme")).Should().NotBeNull();
        (await persistence.GetNodeAsync("workspace/Acme/Team1")).Should().NotBeNull();
        (await persistence.GetNodeAsync("workspace/Acme/Team2")).Should().NotBeNull();
        (await persistence.GetNodeAsync("workspace/Acme/Team1/Alice")).Should().NotBeNull();

        var alice = await persistence.GetNodeAsync("workspace/Acme/Team1/Alice");
        alice!.Name.Should().Be("Alice");
        alice.NodeType.Should().Be("Person");
    }

    [HubFact]
    public async Task CopyNodeTree_SkipsExistingWhenNotForced()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        await SaveNode("org/Acme/Team1", "Team One", "Team");

        // Pre-create target node with different name
        await SaveNode("workspace/Acme", "Existing Acme", "Organization");

        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "org/Acme", "workspace", force: false);

        copied.Should().Be(1); // Only Team1 copied, Acme skipped

        var existing = await persistence.GetNodeAsync("workspace/Acme");
        existing!.Name.Should().Be("Existing Acme"); // Not overwritten
    }

    [HubFact]
    public async Task CopyNodeTree_OverwritesExistingWhenForced()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");

        // Pre-create target node with different name
        await SaveNode("workspace/Acme", "Existing Acme", "Organization");

        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "org/Acme", "workspace", force: true);

        copied.Should().Be(1);

        var overwritten = await persistence.GetNodeAsync("workspace/Acme");
        overwritten!.Name.Should().Be("Acme Corp"); // Overwritten
    }

    [HubFact]
    public async Task CopyNodeTree_ThrowsWhenSourceNotFound()
    {
        var persistence = GetPersistenceService();

        var act = () => NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "nonexistent/path", "workspace", force: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Source node not found*");
    }

    [HubFact]
    public async Task CopyNodeTree_ToEmptyNamespace()
    {
        await SaveNode("org/Acme", "Acme Corp", "Organization");
        await SaveNode("org/Acme/Sub", "Sub Node", "Markdown");
        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "org/Acme", "", force: false);

        copied.Should().Be(2);

        // With empty target namespace, nodes go to root with source relative paths
        (await persistence.GetNodeAsync("Acme")).Should().NotBeNull();
        (await persistence.GetNodeAsync("Acme/Sub")).Should().NotBeNull();
    }

    [HubFact]
    public async Task CopyNodeTree_PreservesContent()
    {
        var content = new Dictionary<string, object?> { ["key"] = "value" };
        await SaveNode("src/Doc", "My Doc", "Markdown", content);
        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "src/Doc", "dest", force: false);

        copied.Should().Be(1);

        var target = await persistence.GetNodeAsync("dest/Doc");
        target.Should().NotBeNull();
        target!.Content.Should().NotBeNull();
    }

    [HubFact]
    public async Task CopyRootLevelNode_ToNamespace()
    {
        await SaveNode("TopLevel", "Top Level Node", "Markdown");
        var persistence = GetPersistenceService();

        var copied = await NodeCopyHelper.CopyNodeTreeAsync(
            persistence, "TopLevel", "workspace", force: false);

        copied.Should().Be(1);

        var target = await persistence.GetNodeAsync("workspace/TopLevel");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Top Level Node");
    }
}

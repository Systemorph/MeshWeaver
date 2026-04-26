using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for <see cref="NodeCopyHelper"/>. Uses the real mesh — no
/// mocks, no direct persistence access. Node creation flows through
/// <see cref="IMeshService.CreateNode"/> (returns IObservable, subscribed at the
/// test edge). Node reads via <c>hub.GetMeshNode(path)</c> (per-node hub
/// MeshNodeReference reducer).
/// </summary>
public class NodeCopyHelperTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private Task CreateNode(string path, string? name = null, string? nodeType = null, object? content = null)
        => MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name ?? path.Split('/').Last(),
            NodeType = nodeType ?? "Markdown",
            Content = content,
            State = MeshNodeState.Active
        }).FirstAsync().ToTask(TestContext.Current.CancellationToken);

    private Task<MeshNode?> GetNode(string path)
        => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(15))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

    private Task<int> CopyTree(string source, string target, bool force)
        => NodeCopyHelper.CopyNodeTree(MeshService, MeshService, Mesh, source, target, force)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

    [Fact]
    public async Task CopySingleNode_ToNewNamespace()
    {
        await CreateNode("org/Acme", "Acme Corp", "Markdown");

        var copied = await CopyTree("org/Acme", "workspace", force: false);

        copied.Should().Be(1);

        var target = await GetNode("workspace/Acme");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Acme Corp");
        target.NodeType.Should().Be("Markdown");
        target.State.Should().Be(MeshNodeState.Active);
    }

    [Fact]
    public async Task CopyNodeTree_WithDescendants()
    {
        await CreateNode("org/Acme", "Acme Corp", "Markdown");
        await CreateNode("org/Acme/Team1", "Team One", "Markdown");
        await CreateNode("org/Acme/Team2", "Team Two", "Markdown");
        await CreateNode("org/Acme/Team1/Alice", "Alice", "Markdown");

        var copied = await CopyTree("org/Acme", "workspace", force: false);

        copied.Should().Be(4);

        (await GetNode("workspace/Acme")).Should().NotBeNull();
        (await GetNode("workspace/Acme/Team1")).Should().NotBeNull();
        (await GetNode("workspace/Acme/Team2")).Should().NotBeNull();
        (await GetNode("workspace/Acme/Team1/Alice")).Should().NotBeNull();

        var alice = await GetNode("workspace/Acme/Team1/Alice");
        alice!.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task CopyNodeTree_SkipsExistingWhenNotForced()
    {
        await CreateNode("org/Acme", "Acme Corp", "Markdown");
        await CreateNode("org/Acme/Team1", "Team One", "Markdown");
        await CreateNode("workspace/Acme", "Existing Acme", "Markdown");

        var copied = await CopyTree("org/Acme", "workspace", force: false);

        copied.Should().Be(1);

        var existing = await GetNode("workspace/Acme");
        existing!.Name.Should().Be("Existing Acme");
    }

    [Fact]
    public async Task CopyNodeTree_OverwritesExistingWhenForced()
    {
        await CreateNode("org/Acme", "Acme Corp", "Markdown");
        await CreateNode("workspace/Acme", "Existing Acme", "Markdown");

        var copied = await CopyTree("org/Acme", "workspace", force: true);

        copied.Should().Be(1);

        var overwritten = await GetNode("workspace/Acme");
        overwritten!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task CopyNodeTree_ThrowsWhenSourceNotFound()
    {
        var act = () => CopyTree("nonexistent/path", "workspace", force: false);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Source node not found*");
    }

    [Fact]
    public async Task CopyNodeTree_ToEmptyNamespace()
    {
        await CreateNode("org/Acme", "Acme Corp", "Markdown");
        await CreateNode("org/Acme/Sub", "Sub Node", "Markdown");

        var copied = await CopyTree("org/Acme", "", force: false);

        copied.Should().Be(2);

        (await GetNode("Acme")).Should().NotBeNull();
        (await GetNode("Acme/Sub")).Should().NotBeNull();
    }

    [Fact]
    public async Task CopyNodeTree_PreservesContent()
    {
        var content = new Dictionary<string, object?> { ["key"] = "value" };
        await CreateNode("src/Doc", "My Doc", "Markdown", content);

        var copied = await CopyTree("src/Doc", "dest", force: false);

        copied.Should().Be(1);

        var target = await GetNode("dest/Doc");
        target.Should().NotBeNull();
        target!.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task CopyRootLevelNode_ToNamespace()
    {
        await CreateNode("TopLevel", "Top Level Node", "Markdown");

        var copied = await CopyTree("TopLevel", "workspace", force: false);

        copied.Should().Be(1);

        var target = await GetNode("workspace/TopLevel");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Top Level Node");
    }
}

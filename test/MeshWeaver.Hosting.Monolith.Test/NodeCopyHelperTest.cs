using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
    // NOTE: not opted into ShareMeshAcrossTests — multiple tests create the same
    // "org/Acme" path and would collide on the shared mesh.

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private async Task CreateNode(string path, string? name = null, string? nodeType = null, object? content = null)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = name ?? path.Split('/').Last(),
            NodeType = nodeType ?? "Markdown",
            Content = content,
            State = MeshNodeState.Active
        };
        // A top-level fixture (empty namespace) is a partition root: the PartitionWriteGuard
        // rejects a normal user creating a non-partition-owning type there. Seed it under the
        // System identity (the legitimate partition provisioner) so the fixture exists for the
        // copy assertions. Nested fixtures create normally under the test (Admin) identity.
        if (string.IsNullOrEmpty(node.Namespace))
            SeedTopLevel(node);
        else
            await MeshService.CreateNode(node).Should().Within(30.Seconds()).Emit();
    }

    private async Task<MeshNode?> GetNode(string path)
        => await Mesh.GetMeshNode(path, TimeSpan.FromSeconds(15))
            .Should().Within(15.Seconds()).Emit();

    // Explicit partition creation — a Space is the sanctioned top-level container. The
    // creator (the test's Admin identity) is granted Admin by SpacePostCreationHandler, so
    // child writes under it authorise.
    private async Task CreateSpace(string id)
        => await MeshService.CreateNode(new MeshNode(id)
        {
            Name = id,
            NodeType = "Space",
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

    private async Task<int> CopyTree(string source, string target, bool force)
        => await NodeCopyHelper.CopyNodeTree(MeshService, MeshService, Mesh, source, target, force)
            .Should().Within(30.Seconds()).Emit();

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
        var notification = await NodeCopyHelper
            .CopyNodeTree(MeshService, MeshService, Mesh, "nonexistent/path", "workspace", force: false)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("Source node not found");
    }

    [Fact]
    public async Task CopyNodeTree_ToSpaceNamespace()
    {
        // The empty namespace is reserved for partitions (PartitionWriteGuard rejects a
        // non-partition Markdown root there), so copy the subtree under an explicitly-created
        // Space — the sanctioned top-level partition — rather than the bare root.
        await CreateSpace("destspace");
        await CreateNode("org/Acme", "Acme Corp", "Markdown");
        await CreateNode("org/Acme/Sub", "Sub Node", "Markdown");

        var copied = await CopyTree("org/Acme", "destspace", force: false);

        copied.Should().Be(2);

        (await GetNode("destspace/Acme")).Should().NotBeNull();
        (await GetNode("destspace/Acme/Sub")).Should().NotBeNull();
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

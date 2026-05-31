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

    private void CreateNode(string path, string? name = null, string? nodeType = null, object? content = null)
        => MeshService.CreateNode(MeshNode.FromPath(path) with
        {
            Name = name ?? path.Split('/').Last(),
            NodeType = nodeType ?? "Markdown",
            Content = content,
            State = MeshNodeState.Active
        }).Should().Within(30.Seconds()).Emit();

    private MeshNode? GetNode(string path)
        => Mesh.GetMeshNode(path, TimeSpan.FromSeconds(15))
            .Should().Within(15.Seconds()).Emit();

    private int CopyTree(string source, string target, bool force)
        => NodeCopyHelper.CopyNodeTree(MeshService, MeshService, Mesh, source, target, force)
            .Should().Within(30.Seconds()).Emit();

    [Fact]
    public void CopySingleNode_ToNewNamespace()
    {
        CreateNode("org/Acme", "Acme Corp", "Markdown");

        var copied = CopyTree("org/Acme", "workspace", force: false);

        copied.Should().Be(1);

        var target = GetNode("workspace/Acme");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Acme Corp");
        target.NodeType.Should().Be("Markdown");
        target.State.Should().Be(MeshNodeState.Active);
    }

    [Fact]
    public void CopyNodeTree_WithDescendants()
    {
        CreateNode("org/Acme", "Acme Corp", "Markdown");
        CreateNode("org/Acme/Team1", "Team One", "Markdown");
        CreateNode("org/Acme/Team2", "Team Two", "Markdown");
        CreateNode("org/Acme/Team1/Alice", "Alice", "Markdown");

        var copied = CopyTree("org/Acme", "workspace", force: false);

        copied.Should().Be(4);

        GetNode("workspace/Acme").Should().NotBeNull();
        GetNode("workspace/Acme/Team1").Should().NotBeNull();
        GetNode("workspace/Acme/Team2").Should().NotBeNull();
        GetNode("workspace/Acme/Team1/Alice").Should().NotBeNull();

        var alice = GetNode("workspace/Acme/Team1/Alice");
        alice!.Name.Should().Be("Alice");
    }

    [Fact]
    public void CopyNodeTree_SkipsExistingWhenNotForced()
    {
        CreateNode("org/Acme", "Acme Corp", "Markdown");
        CreateNode("org/Acme/Team1", "Team One", "Markdown");
        CreateNode("workspace/Acme", "Existing Acme", "Markdown");

        var copied = CopyTree("org/Acme", "workspace", force: false);

        copied.Should().Be(1);

        var existing = GetNode("workspace/Acme");
        existing!.Name.Should().Be("Existing Acme");
    }

    [Fact]
    public void CopyNodeTree_OverwritesExistingWhenForced()
    {
        CreateNode("org/Acme", "Acme Corp", "Markdown");
        CreateNode("workspace/Acme", "Existing Acme", "Markdown");

        var copied = CopyTree("org/Acme", "workspace", force: true);

        copied.Should().Be(1);

        var overwritten = GetNode("workspace/Acme");
        overwritten!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public void CopyNodeTree_ThrowsWhenSourceNotFound()
    {
        var notification = NodeCopyHelper
            .CopyNodeTree(MeshService, MeshService, Mesh, "nonexistent/path", "workspace", force: false)
            .Materialize()
            .Should().Within(30.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("Source node not found");
    }

    [Fact]
    public void CopyNodeTree_ToEmptyNamespace()
    {
        CreateNode("org/Acme", "Acme Corp", "Markdown");
        CreateNode("org/Acme/Sub", "Sub Node", "Markdown");

        var copied = CopyTree("org/Acme", "", force: false);

        copied.Should().Be(2);

        GetNode("Acme").Should().NotBeNull();
        GetNode("Acme/Sub").Should().NotBeNull();
    }

    [Fact]
    public void CopyNodeTree_PreservesContent()
    {
        var content = new Dictionary<string, object?> { ["key"] = "value" };
        CreateNode("src/Doc", "My Doc", "Markdown", content);

        var copied = CopyTree("src/Doc", "dest", force: false);

        copied.Should().Be(1);

        var target = GetNode("dest/Doc");
        target.Should().NotBeNull();
        target!.Content.Should().NotBeNull();
    }

    [Fact]
    public void CopyRootLevelNode_ToNamespace()
    {
        CreateNode("TopLevel", "Top Level Node", "Markdown");

        var copied = CopyTree("TopLevel", "workspace", force: false);

        copied.Should().Be(1);

        var target = GetNode("workspace/TopLevel");
        target.Should().NotBeNull();
        target!.Name.Should().Be("Top Level Node");
    }
}

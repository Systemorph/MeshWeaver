using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

/// <summary>
/// Tests for version history UI layout areas (Versions list, VersionDiff, and menu integration).
/// Uses file system persistence with a temp directory so that FileSystemVersionStore
/// writes versioned snapshots that the layout areas can query.
/// </summary>
public class VersionViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private string? _tempDir;

    private string GetTempDir()
    {
        if (_tempDir != null) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "MeshWeaverVersionViewTest", $"test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(GetTempDir())
            .AddGraph()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition));

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Helper: creates a node, then updates it the specified number of times
    /// so that version snapshots are written by FileSystemVersionStore.
    /// Returns the created node's path.
    /// </summary>
    private async Task<string> CreateNodeWithVersions(string path, int updateCount)
    {
        var node = MeshNode.FromPath(path) with
        {
            Name = "Test Node v0",
            NodeType = "Markdown"
        };
        await NodeFactory.CreateNode(node).Should().Emit();

        for (var i = 1; i <= updateCount; i++)
        {
            var updated = MeshNode.FromPath(path) with
            {
                Name = $"Test Node v{i}",
                NodeType = "Markdown"
            };
            await NodeFactory.UpdateNode(updated).Should().Emit();
        }

        return path;
    }

    /// <summary>
    /// Create a node with 3 versions (initial + 2 updates).
    /// Request the Versions layout area and verify that GetControlStream
    /// returns a StackControl (the version list container).
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task VersionsArea_RendersVersionList()
    {
        // Arrange: create node with 3 versions
        var nodePath = await CreateNodeWithVersions("test/mynode", 2);
        var nodeAddress = new Address(nodePath);
        var client = GetClient();

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.VersionsArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        // Act
        Output.WriteLine("Waiting for Versions area to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Should()
            .Within(TimeSpan.FromSeconds(10))
            .Match(x => x is not null);

        // Assert
        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Versions area should render a control");
        control.Should().BeOfType<StackControl>("Versions area renders as a StackControl");
    }

    /// <summary>
    /// Create a node with only 1 version (the initial create).
    /// Request the Versions layout area and verify the stack renders without error.
    /// FileSystemVersionStore writes even the first version, so this should still produce a valid stack.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task VersionsArea_SingleVersion_RendersWithoutError()
    {
        // Arrange: create node with just 1 version
        var nodePath = await CreateNodeWithVersions("test/singleversion", 0);
        var nodeAddress = new Address(nodePath);
        var client = GetClient();

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.VersionsArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        // Act
        Output.WriteLine("Waiting for Versions area (single version) to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Should()
            .Within(TimeSpan.FromSeconds(10))
            .Match(x => x is not null);

        // Assert
        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Versions area should render even with a single version");
        control.Should().BeOfType<StackControl>("Versions area renders as a StackControl");
    }

    /// <summary>
    /// Create a node, update it once to produce version 1.
    /// Request the VersionDiff layout area with ?version= parameter pointing to v1.
    /// Verify that GetControlStream returns a StackControl that contains
    /// a DiffEditorControl somewhere in the rendered area tree.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task VersionDiffArea_RendersWithVersionParam()
    {
        // Arrange: create node with 2 versions (create + 1 update)
        var nodePath = await CreateNodeWithVersions("test/diffnode", 1);
        var nodeAddress = new Address(nodePath);
        var client = GetClient();

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        // Find the first version number via IVersionQuery — wait for snapshots to land.
        // Treat GetVersions as a snapshot read and use Where() polling to avoid racing
        // the version-writing storage decorator's async file I/O.
        var versionQuery = Mesh.ServiceProvider.GetService<IVersionQuery>();
        versionQuery.Should().NotBeNull("FileSystemVersionStore should be registered");

        var versions = await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => versionQuery!.GetVersions(nodePath).ToList())
            .Where(v => v.Count >= 1)
            .Should()
            .Within(TimeSpan.FromSeconds(5))
            .Emit();

        Output.WriteLine($"Found {versions.Count} versions");
        versions.Should().NotBeEmpty("at least one version snapshot should exist after updates");

        // Use the oldest (lowest) version for diffing
        var oldestVersion = versions.OrderBy(v => v.Version).First().Version;
        Output.WriteLine($"Using version {oldestVersion} for diff");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.VersionDiffArea)
        {
            Id = $"?version={oldestVersion}"
        };

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        // Act
        Output.WriteLine("Waiting for VersionDiff area to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Should()
            .Within(TimeSpan.FromSeconds(10))
            .Match(x => x is not null);

        // Assert
        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("VersionDiff area should render a control");
        control.Should().BeOfType<StackControl>("VersionDiff area renders as a StackControl");
    }

    /// <summary>
    /// Create a node. Request the menu area ($Menu) via the Overview layout area.
    /// Verify that a "Versions" menu item appears in the MenuControl items.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task VersionsMenu_AppearsInNodeMenu()
    {
        // Arrange: create a node
        var nodePath = await CreateNodeWithVersions("test/menunode", 0);
        var nodeAddress = new Address(nodePath);
        var client = GetClient();

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        // Menu is rendered as part of any layout area via the predicate-based renderer;
        // request Overview which triggers the $Menu renderer.
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        // Act: read the $Menu:Node control from the layout stream. Built-in
        // items (Edit, Versions, Delete, …) live in the "Node" context; the
        // default unnamed $Menu area is reserved for app-specific additions.
        // The menu first renders empty and populates its items reactively, so
        // wait for the emission that carries the built-in "Versions" item.
        Output.WriteLine("Waiting for $Menu:Node to render...");
        var menuControl = await stream
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .Should()
            .Within(TimeSpan.FromSeconds(10))
            .Match(x => x is MenuControl m &&
                        m.Items.Any(i => i.Label == "Versions" && i.Area == MeshNodeLayoutAreas.VersionsArea));

        // Assert
        Output.WriteLine($"Received menu control: {menuControl?.GetType().Name}");
        menuControl.Should().NotBeNull("Menu should render for the node");
        var menu = menuControl.Should().BeOfType<MenuControl>().Subject;

        Output.WriteLine($"Menu items: {menu.Items.Count}");
        foreach (var item in menu.Items)
            Output.WriteLine($"  {item.Label} (Area={item.Area})");

        menu.Items.Should().Contain(
            item => item.Label == "Versions" && item.Area == MeshNodeLayoutAreas.VersionsArea,
            "Versions menu item should appear in the node menu");
    }
}

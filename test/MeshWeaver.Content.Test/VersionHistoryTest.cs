using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Content.Test;

public class VersionHistoryTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private string? _tempDir;

    private string GetTempDir()
    {
        if (_tempDir != null) return _tempDir;
        _tempDir = Path.Combine(Path.GetTempPath(), "MeshWeaverVersionTest", $"test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(GetTempDir())
            .AddGraph()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas());

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Polls <see cref="IVersionQuery.GetVersions"/> at a short interval and emits the
    /// first observation that satisfies <paramref name="predicate"/>, then completes.
    /// Treats <c>GetVersions</c> as a benchmark/snapshot stream and uses
    /// <see cref="Observable.Where"/> to wait for the post-write settled state — the
    /// raw call returns whatever's on disk at the moment of invocation and can
    /// race the WriteVersion side of the just-completed save.
    /// </summary>
    private async Task<IList<MeshNodeVersion>> WaitForVersionsAsync(
        string path, Func<IList<MeshNodeVersion>, bool> predicate)
    {
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        return await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => versionQuery.GetVersions(path).ToList())
            .Where(predicate)
            .Timeout(TimeSpan.FromSeconds(5))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 20000)]
    public async Task VersionQuery_GetVersions_ReturnsHistory()
    {
        // Arrange
        var node = MeshNode.FromPath("test/mynode") with { Name = "V1", State = MeshNodeState.Active, NodeType = "Markdown" };
        var created = await CreateNodeAsync(node);

        // Update 3 times
        var updated1 = created with { Name = "V2" };
        await NodeFactory.UpdateNode(updated1);

        var updated2 = updated1 with { Name = "V3" };
        await NodeFactory.UpdateNode(updated2);

        var updated3 = updated2 with { Name = "V4" };
        await NodeFactory.UpdateNode(updated3);

        // Act — wait for at least one snapshot to land (writes are async via the
        // version-writing storage decorator; polling Where() avoids racing them).
        var versions = await WaitForVersionsAsync("test/mynode", v => v.Count >= 1);

        // Assert
        versions.Should().NotBeEmpty("node was created and updated 3 times");
        versions.Should().BeInDescendingOrder(v => v.Version, "versions should be ordered newest first");
    }

    [Fact(Timeout = 20000)]
    public async Task VersionQuery_GetVersionAsync_ReturnsCorrectSnapshot()
    {
        // Arrange
        var node = MeshNode.FromPath("test/snapshot") with { Name = "V1", State = MeshNodeState.Active, NodeType = "Markdown" };
        var created = await CreateNodeAsync(node);

        // Get the version of the first save — wait for it to land
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var options = Mesh.JsonSerializerOptions;

        var versionsAfterCreate = await WaitForVersionsAsync("test/snapshot", v => v.Count >= 1);

        // Update to V2
        var updated = created with { Name = "V2" };
        await NodeFactory.UpdateNode(updated);

        // Act - get the first version
        var firstVersion = versionsAfterCreate.LastOrDefault();
        firstVersion.Should().NotBeNull("there should be at least one version after create");

        var historicalNode = await versionQuery.GetVersion("test/snapshot", firstVersion!.Version, options)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        // Assert
        historicalNode.Should().NotBeNull("the first version should be retrievable");
        historicalNode!.Name.Should().Be("V1", "the first version should have the original name");
    }

    [Fact(Timeout = 20000)]
    public async Task VersionQuery_GetVersionBeforeAsync_FindsPreChangeState()
    {
        // Arrange
        var node = MeshNode.FromPath("test/before") with { Name = "V1", State = MeshNodeState.Active, NodeType = "Markdown" };
        var created = await CreateNodeAsync(node);

        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var options = Mesh.JsonSerializerOptions;

        // Capture version after v1 create — wait for snapshot to land
        var versionsAfterV1 = await WaitForVersionsAsync("test/before", v => v.Count >= 1);
        var v1Version = versionsAfterV1.LastOrDefault();
        v1Version.Should().NotBeNull("there should be a version after create");
        Output.WriteLine($"After Create: versions = [{string.Join(", ", versionsAfterV1.Select(v => v.Version))}]");

        // Update to V2
        await NodeFactory.UpdateNode(created with { Name = "V2" });

        // Update to V3
        await NodeFactory.UpdateNode(created with { Name = "V3" });

        // Capture all versions — wait for all three snapshots to land
        var allVersions = await WaitForVersionsAsync("test/before", v => v.Count >= 3);
        Output.WriteLine($"After all updates: versions = [{string.Join(", ", allVersions.Select(v => v.Version))}]");
        foreach (var v in allVersions)
        {
            var snapshot = await versionQuery.GetVersion("test/before", v.Version, options)
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
            Output.WriteLine($"  Version {v.Version}: Name='{snapshot?.Name}'");
        }
        var v3Version = allVersions.FirstOrDefault();
        v3Version.Should().NotBeNull("there should be a latest version");

        // Act - get the version before v3 (should be v2)
        var beforeV3 = await versionQuery.GetVersionBefore("test/before", v3Version!.Version, options)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        // Assert
        beforeV3.Should().NotBeNull("there should be a version before v3");
        beforeV3!.Name.Should().Be("V2", "the version before v3 should be v2");

        // Act - get the version before v1 (should be null since v1 is the first)
        var beforeV1 = await versionQuery.GetVersionBefore("test/before", v1Version!.Version, options)
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        // Assert
        beforeV1.Should().BeNull("there should be no version before the first one");
    }

    [Fact(Timeout = 20000)]
    public async Task SatelliteContent_IncludedInVersionHistory()
    {
        // Arrange - create a satellite node (MainNode != Path)
        var activityLog = new ActivityLog("TestActivity") { HubPath = "test/primary" };
        var node = MeshNode.FromPath("test/satellite") with
        {
            Name = "Satellite Node",
            State = MeshNodeState.Active,
            Content = activityLog,
            MainNode = "test/primary"
        };
        var created = await CreateNodeAsync(node);

        // Update multiple times
        await NodeFactory.UpdateNode(created with { Name = "Satellite V2" });
        await NodeFactory.UpdateNode(created with { Name = "Satellite V3" });

        // Act — wait for the satellite's snapshot to land
        var versions = await WaitForVersionsAsync("test/satellite", v => v.Count >= 1);

        // Assert - satellite content should now be included in version history
        versions.Should().NotBeEmpty("satellite content nodes should now have version history");
    }

    [Fact(Timeout = 20000)]
    public async Task RollbackNode_RestoresHistoricalState()
    {
        // Arrange
        var node = new MeshNode("rollback", TestPartition) { Name = "Original", NodeType = "Markdown", State = MeshNodeState.Active };
        var created = await CreateNodeAsync(node);

        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var options = Mesh.JsonSerializerOptions;

        // Capture the original version — wait for snapshot to land
        var nodePath = $"{TestPartition}/rollback";
        var versionsAfterCreate = await WaitForVersionsAsync(nodePath, v => v.Count >= 1);
        var originalVersion = versionsAfterCreate.LastOrDefault();
        originalVersion.Should().NotBeNull("there should be a version after create");

        // Update to "Modified"
        await NodeFactory.UpdateNode(created with { Name = "Modified" });

        // Act - post RollbackNodeRequest to the node hub
        var client = GetClient();
        var rollbackRequest = new RollbackNodeRequest(nodePath, originalVersion!.Version);
        client.Post(rollbackRequest, o => o.WithTarget(new Address(nodePath)));

        // Wait for the rollback to be processed
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Assert - verify the node was rolled back (stream read)
        var currentNode = await ReadNodeAsync(nodePath, TestContext.Current.CancellationToken);

        currentNode.Should().NotBeNull("the node should still exist after rollback");
        currentNode!.Name.Should().Be("Original", "the node should have been rolled back to the original name");
    }
}



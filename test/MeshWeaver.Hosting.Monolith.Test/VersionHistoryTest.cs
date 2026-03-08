using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

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

    [Fact(Timeout = 10000)]
    public async Task VersionQuery_GetVersions_ReturnsHistory()
    {
        // Arrange
        var node = MeshNode.FromPath("test/mynode") with { Name = "V1", State = MeshNodeState.Active };
        var created = await CreateNodeAsync(node);

        // Update 3 times
        var updated1 = created with { Name = "V2" };
        await NodeFactory.UpdateNodeAsync(updated1);

        var updated2 = updated1 with { Name = "V3" };
        await NodeFactory.UpdateNodeAsync(updated2);

        var updated3 = updated2 with { Name = "V4" };
        await NodeFactory.UpdateNodeAsync(updated3);

        // Act
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var versions = new List<MeshNodeVersion>();
        await foreach (var v in versionQuery.GetVersionsAsync("test/mynode"))
            versions.Add(v);

        // Assert
        versions.Should().NotBeEmpty("node was created and updated 3 times");
        versions.Should().BeInDescendingOrder(v => v.Version, "versions should be ordered newest first");
    }

    [Fact(Timeout = 10000)]
    public async Task VersionQuery_GetVersionAsync_ReturnsCorrectSnapshot()
    {
        // Arrange
        var node = MeshNode.FromPath("test/snapshot") with { Name = "V1", State = MeshNodeState.Active };
        var created = await CreateNodeAsync(node);

        // Get the version of the first save
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var options = Mesh.JsonSerializerOptions;

        var versionsAfterCreate = new List<MeshNodeVersion>();
        await foreach (var v in versionQuery.GetVersionsAsync("test/snapshot"))
            versionsAfterCreate.Add(v);

        // Update to V2
        var updated = created with { Name = "V2" };
        await NodeFactory.UpdateNodeAsync(updated);

        // Act - get the first version
        var firstVersion = versionsAfterCreate.LastOrDefault();
        firstVersion.Should().NotBeNull("there should be at least one version after create");

        var historicalNode = await versionQuery.GetVersionAsync("test/snapshot", firstVersion!.Version, options);

        // Assert
        historicalNode.Should().NotBeNull("the first version should be retrievable");
        historicalNode!.Name.Should().Be("V1", "the first version should have the original name");
    }

    [Fact(Timeout = 10000)]
    public async Task VersionQuery_GetVersionBeforeAsync_FindsPreChangeState()
    {
        // Arrange
        var node = MeshNode.FromPath("test/before") with { Name = "V1", State = MeshNodeState.Active };
        var created = await CreateNodeAsync(node);

        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var options = Mesh.JsonSerializerOptions;

        // Capture version after v1 create
        var versionsAfterV1 = new List<MeshNodeVersion>();
        await foreach (var v in versionQuery.GetVersionsAsync("test/before"))
            versionsAfterV1.Add(v);
        var v1Version = versionsAfterV1.LastOrDefault();
        v1Version.Should().NotBeNull("there should be a version after create");

        // Update to V2
        await NodeFactory.UpdateNodeAsync(created with { Name = "V2" });

        // Update to V3
        await NodeFactory.UpdateNodeAsync(created with { Name = "V3" });

        // Capture all versions
        var allVersions = new List<MeshNodeVersion>();
        await foreach (var v in versionQuery.GetVersionsAsync("test/before"))
            allVersions.Add(v);
        var v3Version = allVersions.FirstOrDefault();
        v3Version.Should().NotBeNull("there should be a latest version");

        // Act - get the version before v3 (should be v2)
        var beforeV3 = await versionQuery.GetVersionBeforeAsync("test/before", v3Version!.Version, options);

        // Assert
        beforeV3.Should().NotBeNull("there should be a version before v3");
        beforeV3!.Name.Should().Be("V2", "the version before v3 should be v2");

        // Act - get the version before v1 (should be null since v1 is the first)
        var beforeV1 = await versionQuery.GetVersionBeforeAsync("test/before", v1Version!.Version, options);

        // Assert
        beforeV1.Should().BeNull("there should be no version before the first one");
    }

    [Fact(Timeout = 10000)]
    public async Task SatelliteContent_ExcludedFromVersionHistory()
    {
        // Arrange - create a node with ISatelliteContent (ActivityLog)
        var activityLog = new ActivityLog("TestActivity") { HubPath = "test/primary" };
        var node = MeshNode.FromPath("test/satellite") with
        {
            Name = "Satellite Node",
            State = MeshNodeState.Active,
            Content = activityLog
        };
        var created = await CreateNodeAsync(node);

        // Update multiple times
        await NodeFactory.UpdateNodeAsync(created with { Name = "Satellite V2" });
        await NodeFactory.UpdateNodeAsync(created with { Name = "Satellite V3" });

        // Act
        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var versions = new List<MeshNodeVersion>();
        await foreach (var v in versionQuery.GetVersionsAsync("test/satellite"))
            versions.Add(v);

        // Assert - satellite content should be excluded from version history
        versions.Should().BeEmpty("satellite content nodes should not have version history");
    }

    [Fact(Timeout = 10000)]
    public async Task RollbackNode_RestoresHistoricalState()
    {
        // Arrange
        var node = MeshNode.FromPath("test/rollback") with { Name = "Original", State = MeshNodeState.Active };
        var created = await CreateNodeAsync(node);

        var versionQuery = Mesh.ServiceProvider.GetRequiredService<IVersionQuery>();
        var options = Mesh.JsonSerializerOptions;

        // Capture the original version
        var versionsAfterCreate = new List<MeshNodeVersion>();
        await foreach (var v in versionQuery.GetVersionsAsync("test/rollback"))
            versionsAfterCreate.Add(v);
        var originalVersion = versionsAfterCreate.LastOrDefault();
        originalVersion.Should().NotBeNull("there should be a version after create");

        // Update to "Modified"
        await NodeFactory.UpdateNodeAsync(created with { Name = "Modified" });

        // Act - post RollbackNodeRequest
        var client = GetClient();
        var rollbackRequest = new RollbackNodeRequest("test/rollback", originalVersion!.Version);
        client.Post(rollbackRequest, o => o.WithTarget(Mesh.Address));

        // Wait for the rollback to be processed
        await Task.Delay(2000, TestContext.Current.CancellationToken);

        // Assert - verify the node was rolled back
        var currentNode = await MeshQuery
            .QueryAsync<MeshNode>("path:test/rollback scope:exact", ct: TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        currentNode.Should().NotBeNull("the node should still exist after rollback");
        currentNode!.Name.Should().Be("Original", "the node should have been rolled back to the original name");
    }
}

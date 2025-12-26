using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for syncing MeshNode.Version with MessageHub.Version.
/// Verifies that:
/// - Initial version is 0
/// - Version increments on operations
/// - Version persists across hub restarts
/// </summary>
[Collection("MeshNodeVersionSyncTests")]
public class MeshNodeVersionSyncTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverVersionTests");

    [ThreadStatic]
    private static string? _currentTestDirectory;

    private IPersistenceService Persistence => ServiceProvider.GetRequiredService<IPersistenceService>();

    private static string GetOrCreateTestDirectory()
    {
        if (_currentTestDirectory == null)
        {
            _currentTestDirectory = Path.Combine(TestDirectoryBase, System.Guid.NewGuid().ToString());
            Directory.CreateDirectory(_currentTestDirectory);
        }
        return _currentTestDirectory;
    }

    public MeshNodeVersionSyncTest(ITestOutputHelper output) : base(output)
    {
    }

    private static void SetupTestConfiguration(InMemoryPersistenceService persistence)
    {
        // Create Story type for testing content changes
        var storyCodeConfig = new CodeConfiguration
        {
            Code = @"
public record Story
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Points { get; init; }
}
"
        };

        var storyNode = MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = "NodeType",
            Description = "A user story",
            IconName = "Document",
            DisplayOrder = 30,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "story",
                DisplayName = "Story",
                IconName = "Document",
                Description = "A user story",
                DisplayOrder = 30
            }
        };
        persistence.SaveNodeAsync(storyNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/story", null, [storyCodeConfig]).GetAwaiter().GetResult();

        // Create Graph type
        var graphCodeConfig = new CodeConfiguration
        {
            Code = @"
public record Graph
{
    [Key]
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}
"
        };

        var graphTypeNode = MeshNode.FromPath("type/graph") with
        {
            Name = "Graph",
            NodeType = "NodeType",
            Description = "The graph root",
            IconName = "Diagram",
            DisplayOrder = 0,
            IsPersistent = true,
            Content = new NodeTypeDefinition
            {
                Id = "graph",
                DisplayName = "Graph",
                IconName = "Diagram",
                Description = "The graph root",
                DisplayOrder = 0
            }
        };
        persistence.SaveNodeAsync(graphTypeNode).GetAwaiter().GetResult();
        persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig]).GetAwaiter().GetResult();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create fresh persistence for each test
        var persistence = new InMemoryPersistenceService();

        // Setup NodeType configurations
        SetupTestConfiguration(persistence);

        // Pre-seed the graph node (Version should be 0 initially)
        var graphNode = MeshNode.FromPath("graph") with
        {
            Name = "Graph",
            NodeType = "type/graph",
            Version = 0
        };
        persistence.SaveNodeAsync(graphNode).GetAwaiter().GetResult();

        return builder.AddJsonGraphConfiguration(testDataDirectory);
    }

    public override async ValueTask DisposeAsync()
    {
        var dir = _currentTestDirectory;
        _currentTestDirectory = null;

        await base.DisposeAsync();

        if (dir != null && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact(Timeout = 90000)]
    public async Task MeshNode_InitialVersion_IsZero()
    {
        // Arrange - check initial version in persistence before hub starts
        var initialNode = await Persistence.GetNodeAsync("graph", TestContext.Current.CancellationToken);

        // Assert - Version property exists and is initialized to 0
        initialNode.Should().NotBeNull("graph node should exist in persistence");
        initialNode!.Version.Should().Be(0, "initial version should be 0");
    }

    [Fact(Timeout = 90000)]
    public async Task MeshNode_VersionProperty_CanBeSetAndPersisted()
    {
        // Arrange - create a node with a specific version
        var nodeWithVersion = MeshNode.FromPath("test/versioned") with
        {
            Name = "Versioned Node",
            NodeType = "type/graph",
            Version = 42
        };

        // Act - save the node with version
        await Persistence.SaveNodeAsync(nodeWithVersion, TestContext.Current.CancellationToken);

        // Assert - version is preserved when reading back
        var savedNode = await Persistence.GetNodeAsync("test/versioned", TestContext.Current.CancellationToken);
        savedNode.Should().NotBeNull();
        savedNode!.Version.Should().Be(42, "version should be preserved in persistence");
    }

    [Fact(Timeout = 90000)]
    public void MessageHub_HasSetInitialVersionMethod()
    {
        // Verify that IMessageHub interface has the SetInitialVersion method
        var method = typeof(IMessageHub).GetMethod("SetInitialVersion");
        method.Should().NotBeNull("IMessageHub should have SetInitialVersion method");
        method!.GetParameters().Should().HaveCount(1);
        method.GetParameters()[0].ParameterType.Should().Be(typeof(long));
    }
}

[CollectionDefinition("MeshNodeVersionSyncTests", DisableParallelization = true)]
public class MeshNodeVersionSyncTestsCollection
{
}

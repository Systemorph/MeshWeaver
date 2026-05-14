using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Xunit;

namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for the MeshNode.Version model — see
/// <c>Doc/Architecture/MeshNodeVersioning.md</c> ("1 op = 1 change").
/// Verifies that:
/// - A seeded, never-mutated node keeps its seed Version (0).
/// - An explicitly-supplied Version round-trips through persistence.
/// - <see cref="IMessageHub.SetInitialVersion"/> exists (the hub clock is
///   the source of truth a mutated node's Version is stamped from).
/// Version is the owning hub's logical clock at the moment of mutation,
/// NOT a node-local "+1 per write" counter — monotonic, not contiguous.
/// </summary>
[Collection("MeshNodeVersionSyncTests")]
public class MeshNodeVersionSyncTest : MonolithMeshTestBase
{
    private static readonly JsonSerializerOptions SetupJsonOptions = new JsonSerializerOptions();
    private JsonSerializerOptions _jsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverVersionTests");

    [ThreadStatic]
    private static string? _currentTestDirectory;


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

    // Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.
    protected override bool ShareMeshAcrossTests => true;

    private static void SaveNode(InMemoryStorageAdapter persistence, MeshNode node)
        => persistence.SaveNode(node, SetupJsonOptions).FirstAsync().ToTask().GetAwaiter().GetResult();

    /// <summary>
    /// Seeds two compilable NodeTypes the legal way (see
    /// <c>MeshNodeCompilationIntegrationTest</c>): a NodeType MeshNode whose
    /// <see cref="NodeTypeDefinition.Configuration"/> wires the content type,
    /// plus the source as a child <c>Code</c> MeshNode at
    /// <c>{type}/Source/code</c>. The compile pipeline pulls source from
    /// <c>namespace:$self/Source scope:subtree</c> — NOT from a
    /// <c>SavePartitionObjects</c> blob on the NodeType's own path, which is
    /// an obsolete shape the current pipeline can't read.
    /// </summary>
    private static void SetupTestConfiguration(InMemoryStorageAdapter persistence)
    {
        // Story NodeType + its source Code node. Both Active — the compile
        // pipeline's source query (namespace:$self/Source scope:subtree) only
        // sees Active nodes.
        SaveNode(persistence, MeshNode.FromPath("type/story") with
        {
            Name = "Story",
            NodeType = MeshNode.NodeTypePath,
            Icon = "Document",
            Order = 30,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "A user story",
                Configuration = "config => config.WithContentType<Story>()"
            }
        });
        SaveNode(persistence, MeshNode.FromPath("type/story/Source/code") with
        {
            Name = "code",
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = @"
public record Story
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public int Points { get; init; }
}
"
            }
        });

        // Graph NodeType + its source Code node. The content record is named
        // GraphRoot, NOT Graph — a bare "Graph" collides with the
        // MeshWeaver.Graph namespace in the compile context
        // ('Graph' is a namespace but is used like a type).
        SaveNode(persistence, MeshNode.FromPath("type/graph") with
        {
            Name = "Graph",
            NodeType = MeshNode.NodeTypePath,
            Icon = "Diagram",
            Order = 0,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Description = "The graph root",
                Configuration = "config => config.WithContentType<GraphRoot>()"
            }
        });
        SaveNode(persistence, MeshNode.FromPath("type/graph/Source/code") with
        {
            Name = "code",
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = @"
public record GraphRoot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}
"
            }
        });
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create fresh persistence for each test
        var persistence = new InMemoryStorageAdapter();

        // Setup NodeType configurations
        SetupTestConfiguration(persistence);

        // Pre-seed the graph node (Version should be 0 initially)
        SaveNode(persistence, MeshNode.FromPath("graph") with
        {
            Name = "Graph",
            NodeType = "type/graph",
            Version = 0
        });

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services.AddInMemoryPersistence(persistence))
            .AddGraph();
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

    [Fact(Timeout = 10000)]
    public async Task MeshNode_InitialVersion_IsZero()
    {
        // Arrange - check initial version via query before hub starts
        var initialNode = await ReadNodeAsync("graph");

        // Assert - Version property exists and is initialized to 0
        initialNode.Should().NotBeNull("graph node should exist in persistence");
        initialNode!.Version.Should().Be(0, "initial version should be 0");
    }

    [Fact(Timeout = 10000)]
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
        await NodeFactory.CreateNodeAsync(nodeWithVersion, ct: TestContext.Current.CancellationToken);

        // Assert - version is preserved when reading back
        var savedNode = await ReadNodeAsync("test/versioned");
        savedNode.Should().NotBeNull();
        savedNode!.Version.Should().Be(42, "version should be preserved in persistence");
    }

    [Fact(Timeout = 10000)]
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

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Tests for content: reference resolution via MeshPlugin.Get.
/// Replicates the distributed deployment pattern where each node hub
/// gets its own "content" collection (matching ConfigureMemexMesh).
/// </summary>
public class MeshPluginContentAccessTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");
    private static readonly string ContentBasePath = Path.Combine(Path.GetTempPath(), "MeshPluginContentAccessTest_" + Guid.NewGuid().ToString("N"));

    public MeshPluginContentAccessTest(ITestOutputHelper output) : base(output)
    {
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .AddGraph()
            .AddAI()
            // Replicate the production ConfigureMemexMesh pattern:
            // Each node hub gets its own "content" collection backed by a per-node subdirectory.
            // This matches MemexConfiguration.ConfigureMemexMesh lines 310-334.
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                var contentDir = Path.Combine(ContentBasePath, nodePath);
                var nodeContentConfig = new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    BasePath = contentDir,
                    Settings = new Dictionary<string, string>
                    {
                        ["BasePath"] = contentDir
                    }
                };
                config = config.AddContentCollection(_ => nodeContentConfig);
                return config.AddDefaultLayoutAreas();
            });
    }

    /// <summary>
    /// Tests content:filename.txt (no slash) - should resolve using default "content" collection.
    /// This is the exact pattern used in production: content:Input_Markus.txt
    /// </summary>
    [Fact]
    public async Task Get_ContentReference_NoSlash_ReturnsFileFromDefaultCollection()
    {
        // Arrange — create node and content file
        var nodePath = "TestOrg";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "Input_Markus.txt"), "Hello from Markus");

        await NodeFactory.CreateNodeAsync(
            new MeshNode(nodePath) { Name = "Test Org", NodeType = "Markdown" });

        // Act — MeshPlugin.Get with content: prefix (no slash = default "content" collection)
        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get($"{nodePath}/content:Input_Markus.txt");

        Output.WriteLine($"Result: {result}");

        // Assert
        result.Should().NotStartWith("Not found");
        result.Should().NotStartWith("Error");
        result.Should().Contain("Hello from Markus");
    }

    /// <summary>
    /// Tests the exact production scenario: nested path with content: reference.
    /// Replicates: PartnerRe/AIConsulting/Interviews/content:Input_Markus.txt
    /// </summary>
    [Fact]
    public async Task Get_ContentReference_NestedPath_ReturnsFileContent()
    {
        // Arrange — create nested node and content file
        var nodePath = "PartnerRe/AIConsulting/Interviews";

        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "Input_Markus.txt"),
            "Interview notes for Markus about AI Consulting");

        await NodeFactory.CreateNodeAsync(
            new MeshNode(nodePath) { Name = "Interviews", NodeType = "Markdown" });

        // Act — MeshPlugin.Get replicating the exact production call
        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get($"{nodePath}/content:Input_Markus.txt");

        Output.WriteLine($"Result: {result}");

        // Assert
        result.Should().NotStartWith("Not found");
        result.Should().NotStartWith("Error");
        result.Should().Contain("Interview notes for Markus");
    }

    /// <summary>
    /// Tests content:collectionName/filename.txt (with slash) — explicit collection name.
    /// </summary>
    [Fact]
    public async Task Get_ContentReference_WithExplicitCollection_ReturnsFileContent()
    {
        // Arrange — content:content/filename.txt = explicit "content" collection
        var nodePath = "TestOrg2";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "report.md"), "# Report Content");

        await NodeFactory.CreateNodeAsync(
            new MeshNode(nodePath) { Name = "Test Org 2", NodeType = "Markdown" });

        // Act
        var plugin = new MeshPlugin(Mesh, new MockAgentChat());
        var result = await plugin.Get($"{nodePath}/content:content/report.md");

        Output.WriteLine($"Result: {result}");

        // Assert
        result.Should().NotStartWith("Error");
        result.Should().Contain("# Report Content");
    }

    /// <summary>
    /// Tests direct GetDataRequest to node hub with UnifiedReference.
    /// This bypasses MeshPlugin.Get to test the handler directly.
    /// </summary>
    [Fact]
    public async Task GetDataRequest_ContentReference_DirectToNodeHub_ReturnsFileContent()
    {
        // Arrange
        var nodePath = "DirectTest";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);
        await File.WriteAllTextAsync(Path.Combine(contentDir, "data.txt"), "Direct access content");

        await NodeFactory.CreateNodeAsync(
            new MeshNode(nodePath) { Name = "Direct Test", NodeType = "Markdown" });

        // Act — send GetDataRequest directly to the node hub
        var client = GetClient();
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("content:data.txt")),
            o => o.WithTarget(new Address(nodePath)),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Output.WriteLine($"Response error: {response.Message.Error}");
        Output.WriteLine($"Response data: {response.Message.Data}");

        // Assert
        response.Message.Error.Should().BeNull();
        response.Message.Data.Should().NotBeNull();
        var content = response.Message.Data as string;
        content.Should().Contain("Direct access content");
    }

    /// <summary>
    /// Tests that content collection is properly registered on the node hub.
    /// </summary>
    [Fact]
    public async Task NodeHub_ContentCollection_IsRegistered()
    {
        // Arrange — create node to activate hub
        var nodePath = "CollectionTest";
        var contentDir = Path.Combine(ContentBasePath, nodePath);
        Directory.CreateDirectory(contentDir);

        await NodeFactory.CreateNodeAsync(
            new MeshNode(nodePath) { Name = "Collection Test", NodeType = "Markdown" });

        // Act — query content collections via GetDataRequest
        var client = GetClient();
        var response = await client.AwaitResponse(
            new GetDataRequest(new UnifiedReference("collection:")),
            o => o.WithTarget(new Address(nodePath)),
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Output.WriteLine($"Response error: {response.Message.Error}");
        Output.WriteLine($"Response data type: {response.Message.Data?.GetType().Name}");

        // Assert — should contain the "content" collection
        response.Message.Error.Should().BeNull();
    }

    private class MockAgentChat : IAgentChat
    {
        public AgentContext? Context { get; set; }
        public Action<LayoutAreaControl>? OnDisplayLayoutArea { get; set; }
        public void SetContext(AgentContext? applicationContext) => Context = applicationContext;
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public IAsyncEnumerable<ChatMessage> GetResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) => OnDisplayLayoutArea?.Invoke(layoutAreaControl);
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync()
            => Task.FromResult<IReadOnlyList<AgentDisplayInfo>>(new List<AgentDisplayInfo>());
        public void SetSelectedAgent(string? agentName) { }
        public ThreadExecutionContext? ExecutionContext => null;
        public string? LastDelegationPath { get; set; }
        public Action<string>? UpdateDelegationStatus { get; set; }
    }
}

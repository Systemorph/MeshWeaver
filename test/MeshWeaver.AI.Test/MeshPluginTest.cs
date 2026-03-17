#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.AI.Persistence;
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
/// Integration tests for MeshPlugin CRUD operations and tool creation.
/// Uses MonolithMeshTestBase with file system persistence from test data.
/// </summary>
public class MeshPluginTest : MonolithMeshTestBase
{
    private static readonly string TestDataPath = Path.Combine(AppContext.BaseDirectory, "TestData");

    public MeshPluginTest(ITestOutputHelper output) : base(output) { }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                services.AddMemoryChatPersistence();
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    #region CreateTools / CreateAllTools

    [Fact]
    public void CreateTools_ShouldReturnReadOnlyTools()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var tools = plugin.CreateTools();

        tools.Should().NotBeNull();
        // Read-only tools: Get, Search, NavigateTo
        tools.Should().HaveCount(3);

        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        toolNames.Should().Contain("Get");
        toolNames.Should().Contain("Search");
        toolNames.Should().Contain("NavigateTo");
        toolNames.Should().NotContain("Create");
        toolNames.Should().NotContain("Update");
        toolNames.Should().NotContain("Delete");
    }

    [Fact]
    public void CreateAllTools_ShouldIncludeWriteOperations()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var tools = plugin.CreateAllTools();

        tools.Should().NotBeNull();
        // All tools: Get, Search, NavigateTo, Create, Update, Delete
        tools.Should().HaveCount(6);

        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        toolNames.Should().Contain("Get");
        toolNames.Should().Contain("Search");
        toolNames.Should().Contain("NavigateTo");
        toolNames.Should().Contain("Create");
        toolNames.Should().Contain("Update");
        toolNames.Should().Contain("Delete");
    }

    #endregion

    #region Get

    [Fact]
    public async Task Get_ExistingNode_ReturnsJsonWithNodeProperties()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // Create a node first, then get it (static agent nodes are not queryable via meshQuery)
        var uniqueId = $"GetTest_{Guid.NewGuid():N}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Get Test Node",
            nodeType = "Markdown"
        });
        await plugin.Create(createJson);

        var result = await plugin.Get($"@ACME/{uniqueId}");

        result.Should().NotBeNullOrEmpty();
        result.Should().NotStartWith("Not found");
        result.Should().NotStartWith("Error");

        // Should be valid JSON
        var doc = JsonDocument.Parse(result);
        doc.Should().NotBeNull();
    }

    [Fact]
    public async Task Get_NonExistentNode_ReturnsNotFound()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Get("@NonExistent/Path/Node");

        result.Should().StartWith("Not found");
    }

    [Fact]
    public async Task Get_Children_ReturnsChildArray()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // Agent/* should return child agents
        var result = await plugin.Get("@Agent/*");

        result.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);

        var children = doc.RootElement.EnumerateArray().ToList();
        children.Should().NotBeEmpty("Agent should have child nodes");
        // Each child should have path, name, nodeType
        foreach (var child in children)
        {
            child.TryGetProperty("path", out _).Should().BeTrue();
            child.TryGetProperty("name", out _).Should().BeTrue();
            child.TryGetProperty("nodeType", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Get_WithAtPrefix_ResolvesCorrectly()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // Create a node first
        var uniqueId = $"AtTest_{Guid.NewGuid():N}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "At Prefix Test",
            nodeType = "Markdown"
        });
        await plugin.Create(createJson);

        // With @ prefix
        var result1 = await plugin.Get($"@ACME/{uniqueId}");
        // Without @ prefix (should also work)
        var result2 = await plugin.Get($"ACME/{uniqueId}");

        result1.Should().Be(result2, "@ prefix should be stripped and both should return the same result");
    }

    #endregion

    #region Search

    [Fact]
    public async Task Search_ByNodeType_ReturnsMatchingNodes()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Search("nodeType:Agent");

        result.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(result);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);

        var nodes = doc.RootElement.EnumerateArray().ToList();
        nodes.Should().NotBeEmpty("should find Agent nodes");

        // All results should have nodeType Agent
        foreach (var node in nodes)
        {
            node.GetProperty("nodeType").GetString().Should().Be("Agent");
        }
    }

    [Fact]
    public async Task Search_WithBasePath_ScopesResults()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Search("namespace:", "@Agent");

        result.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(result);
        var nodes = doc.RootElement.EnumerateArray().ToList();
        nodes.Should().NotBeEmpty("Agent should have children");

        // All results should have paths under Agent/
        foreach (var node in nodes)
        {
            node.GetProperty("path").GetString().Should().StartWith("Agent/");
        }
    }

    [Fact]
    public async Task Search_NoResults_ReturnsEmptyArray()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Search("nodeType:CompletelyMadeUpNodeType");

        result.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(result);
        doc.RootElement.EnumerateArray().ToList().Should().BeEmpty();
    }

    #endregion

    #region Create

    [Fact]
    public async Task Create_ValidNode_ReturnsCreatedPath()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);
        var uniqueId = $"TestNode_{Guid.NewGuid():N}";

        var nodeJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Test Created Node",
            nodeType = "Markdown"
        });

        var result = await plugin.Create(nodeJson);

        result.Should().StartWith("Created:");
        result.Should().Contain($"ACME/{uniqueId}");

        // Verify we can retrieve the created node
        var getResult = await plugin.Get($"@ACME/{uniqueId}");
        getResult.Should().NotStartWith("Not found");
    }

    [Fact]
    public async Task Create_InvalidJson_ReturnsError()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Create("{ invalid json }}}");

        result.Should().StartWith("Invalid JSON:");
    }

    [Fact]
    public async Task Create_NullDeserialization_ReturnsError()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Create("null");

        result.Should().Contain("null");
    }

    #endregion

    #region Update

    [Fact]
    public async Task Update_ExistingNode_UpdatesSuccessfully()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // First create a node to update
        var uniqueId = $"UpdateTest_{Guid.NewGuid():N}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Original Name",
            nodeType = "Markdown"
        });
        await plugin.Create(createJson);

        // Now update it
        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = uniqueId,
                @namespace = "ACME",
                name = "Updated Name",
                nodeType = "Markdown"
            }
        });

        var result = await plugin.Update(updateJson);

        result.Should().Contain("Updated:");
        result.Should().Contain($"ACME/{uniqueId}");

        // Verify the update
        var getResult = await plugin.Get($"@ACME/{uniqueId}");
        getResult.Should().Contain("Updated Name");
    }

    [Fact]
    public async Task Update_InvalidJson_ReturnsError()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Update("not valid json");

        result.Should().StartWith("Invalid JSON:");
    }

    [Fact]
    public async Task Update_EmptyArray_ReturnsNoNodesProvided()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Update("[]");

        result.Should().Be("No nodes provided.");
    }

    #endregion

    #region Delete

    [Fact]
    public async Task Delete_ExistingNode_DeletesSuccessfully()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // First create a node to delete
        var uniqueId = $"DeleteTest_{Guid.NewGuid():N}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Node To Delete",
            nodeType = "Markdown"
        });
        await plugin.Create(createJson);

        // Delete it
        var pathsJson = JsonSerializer.Serialize(new[] { $"ACME/{uniqueId}" });
        var result = await plugin.Delete(pathsJson);

        result.Should().Contain("Deleted:");
        result.Should().Contain($"ACME/{uniqueId}");

        // Verify it's gone
        var getResult = await plugin.Get($"@ACME/{uniqueId}");
        getResult.Should().StartWith("Not found");
    }

    [Fact]
    public async Task Delete_InvalidJson_ReturnsError()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Delete("not valid json");

        result.Should().StartWith("Invalid JSON:");
    }

    [Fact]
    public async Task Delete_EmptyArray_ReturnsNoPathsProvided()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = await plugin.Delete("[]");

        result.Should().Be("No paths provided.");
    }

    [Fact]
    public async Task Delete_WithAtPrefix_ResolvesPath()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // Create a node
        var uniqueId = $"DeleteAtTest_{Guid.NewGuid():N}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "Node With At Prefix",
            nodeType = "Markdown"
        });
        await plugin.Create(createJson);

        // Delete with @ prefix in the path
        var pathsJson = JsonSerializer.Serialize(new[] { $"@ACME/{uniqueId}" });
        var result = await plugin.Delete(pathsJson);

        result.Should().Contain("Deleted:");
    }

    #endregion

    #region NavigateTo

    [Fact]
    public void NavigateTo_ValidPath_ReturnsNavigatingMessage()
    {
        var displayedControls = new List<LayoutAreaControl>();
        var mockChat = new MockAgentChat
        {
            OnDisplayLayoutArea = control => displayedControls.Add(control)
        };
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = plugin.NavigateTo("@Agent/Navigator");

        result.Should().Contain("Navigating to:");
        result.Should().Contain("Agent/Navigator");
        displayedControls.Should().HaveCount(1, "DisplayLayoutArea should have been called once");
    }

    [Fact]
    public void NavigateTo_StripsAtPrefix()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var result = plugin.NavigateTo("@ACME/ProductLaunch");

        result.Should().Contain("ACME/ProductLaunch");
        result.Should().NotContain("@ACME");
    }

    #endregion

    #region CRUD Workflow (Create -> Get -> Update -> Delete)

    [Fact]
    public async Task FullCrudWorkflow_CreateGetUpdateDelete()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);
        var uniqueId = $"CrudTest_{Guid.NewGuid():N}";
        var path = $"ACME/{uniqueId}";

        // 1. Create
        var createJson = JsonSerializer.Serialize(new
        {
            id = uniqueId,
            @namespace = "ACME",
            name = "CRUD Test Node",
            nodeType = "Markdown"
        });
        var createResult = await plugin.Create(createJson);
        createResult.Should().StartWith("Created:");

        // 2. Get
        var getResult = await plugin.Get($"@{path}");
        getResult.Should().NotStartWith("Not found");
        getResult.Should().Contain("CRUD Test Node");

        // 3. Update (Get -> modify -> Update pattern)
        var updateJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                id = uniqueId,
                @namespace = "ACME",
                name = "Updated CRUD Test Node",
                nodeType = "Markdown"
            }
        });
        var updateResult = await plugin.Update(updateJson);
        updateResult.Should().Contain("Updated:");

        // 4. Verify update
        var getAfterUpdate = await plugin.Get($"@{path}");
        getAfterUpdate.Should().Contain("Updated CRUD Test Node");

        // 5. Delete
        var deleteResult = await plugin.Delete(JsonSerializer.Serialize(new[] { path }));
        deleteResult.Should().Contain("Deleted:");

        // 6. Verify deletion
        var getAfterDelete = await plugin.Get($"@{path}");
        getAfterDelete.Should().StartWith("Not found");
    }

    #endregion

    #region Write Tool Wiring

    [Fact]
    public async Task WriteToolWiring_ExecutorAgent_GetsWriteTools()
    {
        // The Executor agent description contains "create, update, and delete"
        // so it should get CreateAllTools() (6 tools) rather than CreateTools() (3 tools)
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME/ProductLaunch");

        var agents = await chatClient.GetOrderedAgentsAsync();

        var executor = agents.FirstOrDefault(a => a.Name == "Executor");
        executor.Should().NotBeNull("Executor agent should be loaded from test data");
        executor!.AgentConfiguration.Should().NotBeNull();
        executor.AgentConfiguration!.Description.Should().Contain("create",
            "Executor description should mention create to trigger write tool wiring");
    }

    [Fact]
    public async Task WriteToolWiring_NavigatorAgent_DoesNotGetWriteTools()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.InitializeAsync("ACME/ProductLaunch");

        var agents = await chatClient.GetOrderedAgentsAsync();

        var navigator = agents.FirstOrDefault(a => a.Name == "Navigator");
        navigator.Should().NotBeNull("Navigator agent should be loaded from test data");
        navigator!.AgentConfiguration.Should().NotBeNull();
        // Navigator description says "Understands user intent, navigates the mesh, and delegates"
        // which does NOT contain create/update/delete
        navigator.AgentConfiguration!.Description.Should().NotContain("create");
        navigator.AgentConfiguration!.Description.Should().NotContain("delete");
    }

    #endregion

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
    }
}

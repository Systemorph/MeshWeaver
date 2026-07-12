#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.AI.Persistence;
using MeshWeaver.ContentCollections;
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
    private static readonly string ContentTestPath = Path.Combine(Path.GetTempPath(), "MeshPluginTest_Content");

    public MeshPluginTest(ITestOutputHelper output) : base(output)
    {
        // Set up test content directory with sample files
        if (Directory.Exists(ContentTestPath))
            Directory.Delete(ContentTestPath, true);
        Directory.CreateDirectory(ContentTestPath);
        File.WriteAllText(Path.Combine(ContentTestPath, "readme.md"), "# Hello\nThis is a test file.");
        File.WriteAllText(Path.Combine(ContentTestPath, "data.json"), "{\"key\": \"value\"}");
        var subDir = Path.Combine(ContentTestPath, "images");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "logo.svg"), "<svg></svg>");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(TestDataPath)
            .ConfigureServices(services =>
            {
                return services;
            })
            .AddGraph()
            .AddAI()
            .ConfigureDefaultNodeHub(config => config
                .AddDefaultLayoutAreas()
                .AddFileSystemContentCollection("test-content", _ => ContentTestPath));
    }

    #region CreateTools / CreateAllTools

    [Fact]
    public void CreateTools_ShouldReturnReadOnlyTools()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var tools = plugin.CreateTools();

        tools.Should().NotBeNull();
        // Read-only tools: Get, Search, NavigateTo, GetDiagnostics, RunTests
        tools.Should().HaveCount(5);

        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        toolNames.Should().Contain("Get");
        toolNames.Should().Contain("Search");
        toolNames.Should().Contain("NavigateTo");
        toolNames.Should().Contain("GetDiagnostics");
        toolNames.Should().Contain("RunTests");
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

        // Name asserts FIRST so a missing tool is named in the failure (a bare count
        // mismatch hides which one vanished), count check last.
        var toolNames = tools.OfType<AIFunction>().Select(t => t.Name).ToList();
        toolNames.Should().Contain("Get");
        toolNames.Should().Contain("Search");
        toolNames.Should().Contain("NavigateTo");
        toolNames.Should().Contain("Create");
        toolNames.Should().Contain("Update");
        toolNames.Should().Contain("Patch");
        toolNames.Should().Contain("EditContent");
        toolNames.Should().Contain("Delete");
        toolNames.Should().Contain("Move");
        toolNames.Should().Contain("Copy");
        toolNames.Should().Contain("GetDiagnostics");
        toolNames.Should().Contain("Recycle");
        toolNames.Should().Contain("RunTests");

        // All tools: Get, Search, NavigateTo, Create, Update, Patch, EditContent, Delete, Move, Copy, GetDiagnostics, Recycle, RunTests
        // (RunTests is present because tests run inside the repo — it is omitted in deployed containers.)
        tools.Should().HaveCount(13);
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
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
            "search returns a {count, limit, truncated, results} envelope so truncation is visible");

        var nodes = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
        nodes.Should().NotBeEmpty("should find Agent nodes");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(nodes.Count);

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
        var nodes = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
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
        doc.RootElement.GetProperty("results").EnumerateArray().ToList().Should().BeEmpty();
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
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
                nodeType = "Markdown",
                content = new { text = "updated" }
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
    public async Task NavigateTo_ValidPath_ResolvesAndReturnsNavigatingMessage()
    {
        var displayedControls = new List<LayoutAreaControl>();
        var mockChat = new MockAgentChat
        {
            OnDisplayLayoutArea = control => displayedControls.Add(control)
        };
        var plugin = new MeshPlugin(Mesh, mockChat);

        // NavigateTo now RESOLVES against the live mesh (honest — no false success), so target must exist.
        var uniqueId = $"NavTarget_{Guid.NewGuid():N}";
        var path = $"ACME/{uniqueId}";
        await plugin.Create(JsonSerializer.Serialize(new
        {
            id = uniqueId, @namespace = "ACME", name = "Nav Target", nodeType = "Markdown"
        }));

        var result = await plugin.NavigateTo($"@{path}");

        result.Should().Contain("Navigating to:");
        result.Should().Contain(path);
        displayedControls.Should().HaveCount(1, "DisplayLayoutArea should have been called once");
    }

    [Fact]
    public async Task NavigateTo_StripsAtPrefix()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var uniqueId = $"NavAt_{Guid.NewGuid():N}";
        var path = $"ACME/{uniqueId}";
        await plugin.Create(JsonSerializer.Serialize(new
        {
            id = uniqueId, @namespace = "ACME", name = "Nav At", nodeType = "Markdown"
        }));

        var result = await plugin.NavigateTo($"@{path}");

        result.Should().Contain(path);
        result.Should().NotContain("@ACME");
    }

    [Fact]
    public async Task NavigateTo_NonExistentPath_IsHonest_DoesNotClaimSuccess()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // A path that cannot resolve to anything — the old tool falsely said "Navigating to: …".
        var result = await plugin.NavigateTo("@ACME/there-is-no-such-node-xyz-9999");

        result.Should().NotContain("Navigating to: ACME/there-is-no-such-node-xyz-9999",
            "the tool must never claim to open a node that doesn't exist");
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
                nodeType = "Markdown",
                content = new { text = "updated" }
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
    public async Task WriteToolWiring_WorkerAgent_GetsWriteTools()
    {
        // The Worker agent uses explicit Plugins (Mesh, WebSearch, etc.)
        // which gives it all tools including write operations
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.Initialize("ACME/ProductLaunch").WhenInitialized.FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var agents = await chatClient.GetOrderedAgentsAsync();

        var worker = agents.FirstOrDefault(a => a.Name == "Worker");
        worker.Should().NotBeNull("Worker agent should be loaded from test data");
        worker!.AgentConfiguration.Should().NotBeNull();
        worker.AgentConfiguration!.Plugins.Should().NotBeEmpty(
            "Worker should have explicit plugins configured for write tool access");
    }

    [Fact]
    public async Task WriteToolWiring_AssistantAgent_DescriptionDoesNotMentionCreateOrDelete()
    {
        var chatClient = new AgentChatClient(Mesh.ServiceProvider);
        await chatClient.Initialize("ACME/ProductLaunch").WhenInitialized.FirstAsync().ToTask(TestContext.Current.CancellationToken);

        var agents = await chatClient.GetOrderedAgentsAsync();

        var assistant = agents.FirstOrDefault(a => a.Name == "Assistant");
        assistant.Should().NotBeNull("Assistant agent should be loaded from test data");
        assistant!.AgentConfiguration.Should().NotBeNull();
        // Assistant description focuses on "owning the conversation's red line"
        // and intentionally does not list write verbs in the description copy.
        assistant.AgentConfiguration!.Description.Should().NotContain("create");
        assistant.AgentConfiguration!.Description.Should().NotContain("delete");
    }

    #endregion

    #region Content Collections

    /// <summary>
    /// Gets or creates a node hub with content collections configured.
    /// Uses GetHostedHub with explicit config since ConfigureDefaultNodeHub
    /// is only applied by the routing service for incoming messages.
    /// </summary>
    private IMessageHub GetContentHub()
        => Mesh.GetHostedHub(new Address("ContentTest"),
            config => config.AddFileSystemContentCollection("test-content", _ => ContentTestPath));

    [Fact]
    public void ContentService_ListsCollectionConfigs()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetService<IContentService>();
        contentService.Should().NotBeNull("IContentService should be registered on node hub");

        var configs = contentService!.GetAllCollectionConfigs();
        configs.Should().Contain(c => c.Name == "test-content",
            "test-content collection should be registered");
    }

    [Fact]
    public async Task ContentService_BrowseRootFiles()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollection("test-content").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        collection.Should().NotBeNull();

        var ct = TestContext.Current.CancellationToken;
        var files = await collection!.GetFiles("/").ToList().FirstAsync().ToTask(ct);
        files.Should().Contain(f => f.Name == "readme.md");
        files.Should().Contain(f => f.Name == "data.json");
    }

    [Fact]
    public async Task ContentService_BrowseFolders()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
        var ct = TestContext.Current.CancellationToken;
        var collection = await contentService.GetCollection("test-content").FirstAsync().ToTask(ct);
        collection.Should().NotBeNull();

        var folders = await collection!.GetFolders("/").ToList().FirstAsync().ToTask(ct);
        folders.Should().Contain(f => f.Name == "images");
    }

    [Fact]
    public async Task ContentService_BrowseSubfolderFiles()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
        var ct = TestContext.Current.CancellationToken;
        var collection = await contentService.GetCollection("test-content").FirstAsync().ToTask(ct);
        collection.Should().NotBeNull();

        var files = await collection!.GetFiles("/images").ToList().FirstAsync().ToTask(ct);
        files.Should().Contain(f => f.Name == "logo.svg");
    }

    [Fact]
    public async Task ContentService_UploadAndBrowse()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();
        var collection = await contentService.GetCollection("test-content").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        collection.Should().NotBeNull();

        // Upload a new file
        var content = "uploaded content"u8.ToArray();
        using var stream = new MemoryStream(content);
        await collection!.SaveFile("/", "uploaded.txt", stream).ToTask(TestContext.Current.CancellationToken);

        // Verify it shows up in browsing
        var ct = TestContext.Current.CancellationToken;
        var files = await collection.GetFiles("/").ToList().FirstAsync().ToTask(ct);
        files.Should().Contain(f => f.Name == "uploaded.txt");

        // Clean up
        await collection.DeleteFile("/uploaded.txt").ToTask(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ContentCollection_GetFileContent()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        // Read file content directly via service
        await using var stream = await contentService.GetContent("test-content", "/readme.md").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        text.Should().Contain("Hello");
        text.Should().Contain("test file");
    }

    [Fact]
    public async Task ContentCollection_GetSubfolderFileContent()
    {
        var hub = GetContentHub();
        var contentService = hub.ServiceProvider.GetRequiredService<IContentService>();

        await using var stream = await contentService.GetContent("test-content", "/images/logo.svg").FirstAsync().ToTask(TestContext.Current.CancellationToken);
        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!);
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        text.Should().Contain("svg");
    }

    #endregion

    #region GetDiagnostics / compilation-error surfacing

    /// <summary>
    /// A NodeType whose Configuration references an undefined type must surface
    /// the compilation error through <see cref="MeshPlugin.GetDiagnostics"/> so
    /// a code-authoring agent can self-diagnose.
    /// </summary>
    // Skipped 2026-04-28: same Windows file-locking + concurrent-compile race as
    // CodeEditRecompileTest. The first compile attempt writes the cached .dll +
    // loads it into the AssemblyLoadContext; a second compile attempt on the same
    // path then fails File.Create with "being used by another process", and the
    // IOException short-circuits CompileAsync before Roslyn ever surfaces the
    // expected "ThisTypeDoesNotExist" error. NodeTypeService DOES now cache the
    // IOException as the compilation error (so GetDiagnostics returns Error, not
    // Unknown), but the test asserts the *Roslyn* message, which never reaches it.
    // Re-enable once compile-on-broken-NodeType is serialised against in-flight
    // compiles AND DLL files are released after Emit failures (mirror the
    // CodeEditRecompileTest fix).
    [Fact(Timeout = 60000)]
    public async Task GetDiagnostics_BrokenNodeType_ReturnsErrorStatus()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        var nodeTypeId = $"BrokenType_{Guid.NewGuid():N}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = nodeTypeId,
            @namespace = "ACME",
            name = "Broken Type",
            nodeType = "NodeType",
            content = new
            {
                type = "NodeTypeDefinition",
                configuration = "config => config.WithContentType<ThisTypeDoesNotExist>()"
            }
        });
        // Wrap "type" â†’ "$type" for JsonPolymorphic
        createJson = createJson.Replace("\"type\":", "\"$type\":");
        await plugin.Create(createJson);

        // Force compilation via the hub. Touching the NodeType path via the hub
        // triggers compile; the error is then recorded on the NodeType MeshNode.
        var nodeTypePath = $"ACME/{nodeTypeId}";
        var resolver = Mesh.ServiceProvider.GetRequiredService<INodeConfigurationResolver>();
        try
        {
            await resolver.ResolveConfiguration(
                    new MeshNode(nodeTypeId, "ACME") { NodeType = nodeTypePath })
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
        }
        catch
        {
            // Expected: compilation throws; the NodeType MeshNode records the error.
        }
        // Done in the test-only "force compile" preamble.

        var diagnosticsJson = await plugin.GetDiagnostics($"@{nodeTypePath}");
        diagnosticsJson.Should().NotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(diagnosticsJson);
        var root = doc.RootElement;
        root.GetProperty("status").GetString()
            .Should().Be("Error", "because the broken NodeType should report compile failure");
        root.GetProperty("error").GetString()
            .Should().Contain("ThisTypeDoesNotExist",
                "the cached error must include the unresolved type");
    }

    /// <summary>
    /// <see cref="MeshPlugin.Get"/> on a broken NodeType (whose per-node hub
    /// can't activate because Roslyn rejects its <c>HubConfiguration</c>) must
    /// fall back to a catalog read and wrap the response with a
    /// <c>compilationError</c> field. The Observe call no longer leaks when
    /// the per-node hub is dead because <c>MeshOperations.GetWithBrokenNodeTypeFallback</c>
    /// kicks in after the 10s GetDataRequest timeout.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Get_InstanceOfBrokenNodeType_WrapsResponseWithCompilationError()
    {
        var mockChat = new MockAgentChat();
        var plugin = new MeshPlugin(Mesh, mockChat);

        // Create a broken NodeType and force its compilation to cache the error.
        var nodeTypeId = $"BrokenType2_{Guid.NewGuid():N}";
        var nodeTypePath = $"ACME/{nodeTypeId}";
        var createJson = JsonSerializer.Serialize(new
        {
            id = nodeTypeId,
            @namespace = "ACME",
            name = "Broken Type 2",
            nodeType = "NodeType",
            content = new
            {
                type = "NodeTypeDefinition",
                configuration = "config => config.WithContentType<AlsoNotAType>()"
            }
        }).Replace("\"type\":", "\"$type\":");
        await plugin.Create(createJson);

        var resolver = Mesh.ServiceProvider.GetRequiredService<INodeConfigurationResolver>();
        try
        {
            await resolver.ResolveConfiguration(
                    new MeshNode(nodeTypeId, "ACME") { NodeType = nodeTypePath })
                .FirstAsync()
                .ToTask(TestContext.Current.CancellationToken);
        }
        catch { /* expected */ }

        // Get the NodeType itself â€” response should include compilationError.
        var result = await plugin.Get($"@{nodeTypePath}");
        result.Should().NotBeNullOrEmpty();

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.TryGetProperty("compilationError", out var err).Should().BeTrue(
            "Get on a broken NodeType must include a compilationError field");
        err.GetString().Should().Contain("AlsoNotAType");
        root.TryGetProperty("node", out _).Should().BeTrue(
            "the original node payload must still be included under 'node'");
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
        public ThreadExecutionContext? ExecutionContext => null;
        public string? LastDelegationPath { get; set; }
        public Action<string>? UpdateDelegationStatus { get; set; }
    }
}

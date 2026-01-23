using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Layout;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Integration tests for creatable types functionality in NodeTypeService using ACME-like sample data.
/// Tests the full algorithm with real persistence and queries.
/// </summary>
[Collection("CreatableTypesIntegrationTests")]
public class CreatableTypesIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverCreatableTypesTests");
    private string? _testDirectory;

    private IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
    private INodeTypeService NodeTypeService => Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
    private IMeshCatalog Catalog => Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

    private string GetOrCreateTestDirectory()
    {
        if (_testDirectory == null)
        {
            _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDirectory);
        }
        return _testDirectory;
    }

    public CreatableTypesIntegrationTest(ITestOutputHelper output) : base(output)
    {
    }

    private static void SetupTestData(InMemoryPersistenceService persistence)
    {
        // Create Organization type (global - can be created at root level)
        var orgTypeDef = new NodeTypeDefinition
        {
            Id = "Organization",
            Namespace = "",
            DisplayName = "Organization",
            Description = "An organization namespace",
            Icon = "Building"
        };
        var orgTypeNode = MeshNode.FromPath("Organization") with
        {
            Name = "Organization",
            NodeType = "NodeType",
            Content = orgTypeDef,
            IsPersistent = true
        };
        persistence.SaveNodeAsync(orgTypeNode).GetAwaiter().GetResult();

        // Create ACME organization (instance of Organization)
        var acmeNode = MeshNode.FromPath("ACME") with
        {
            Name = "ACME Corp",
            NodeType = "Organization",
            Description = "A sample organization",
            IsPersistent = true
        };
        persistence.SaveNodeAsync(acmeNode).GetAwaiter().GetResult();

        // Create ACME/Project type (can be created inside ACME)
        var projectTypeDef = new NodeTypeDefinition
        {
            Id = "Project",
            Namespace = "ACME",
            DisplayName = "Project",
            Description = "A project within ACME",
            Icon = "Briefcase"
        };
        var projectTypeNode = MeshNode.FromPath("ACME/Project") with
        {
            Name = "Project",
            NodeType = "NodeType",
            Content = projectTypeDef,
            IsPersistent = true
        };
        persistence.SaveNodeAsync(projectTypeNode).GetAwaiter().GetResult();

        // Create ACME/Project/Todo type (can be created inside ACME/Project instances)
        var todoTypeDef = new NodeTypeDefinition
        {
            Id = "Todo",
            Namespace = "ACME/Project",
            DisplayName = "Todo",
            Description = "A todo item in a project",
            Icon = "Checkmark"
        };
        var todoTypeNode = MeshNode.FromPath("ACME/Project/Todo") with
        {
            Name = "Todo",
            NodeType = "NodeType",
            Content = todoTypeDef,
            IsPersistent = true
        };
        persistence.SaveNodeAsync(todoTypeNode).GetAwaiter().GetResult();

        // Create ACME/ProductLaunch (instance of ACME/Project)
        var productLaunchNode = MeshNode.FromPath("ACME/ProductLaunch") with
        {
            Name = "Product Launch",
            NodeType = "ACME/Project",
            Description = "2024 Product Launch Project",
            IsPersistent = true
        };
        persistence.SaveNodeAsync(productLaunchNode).GetAwaiter().GetResult();

        // Create global Markdown type
        var markdownTypeDef = new NodeTypeDefinition
        {
            Id = "Markdown",
            Namespace = "",
            DisplayName = "Markdown",
            Description = "A markdown document",
            Icon = "Document",
            DisplayOrder = 1000
        };
        var markdownTypeNode = MeshNode.FromPath("Markdown") with
        {
            Name = "Markdown",
            NodeType = "NodeType",
            Content = markdownTypeDef,
            IsPersistent = true
        };
        persistence.SaveNodeAsync(markdownTypeNode).GetAwaiter().GetResult();

        // Create global NodeType type
        var nodeTypeTypeDef = new NodeTypeDefinition
        {
            Id = "NodeType",
            Namespace = "",
            DisplayName = "NodeType",
            Description = "A node type definition",
            Icon = "Code",
            DisplayOrder = 1001
        };
        var nodeTypeTypeNode = MeshNode.FromPath("NodeType") with
        {
            Name = "NodeType",
            NodeType = "NodeType",
            Content = nodeTypeTypeDef,
            IsPersistent = true
        };
        persistence.SaveNodeAsync(nodeTypeTypeNode).GetAwaiter().GetResult();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var testDataDirectory = GetOrCreateTestDirectory();

        // Create in-memory persistence and pre-seed with test data
        var persistence = new InMemoryPersistenceService();

        // Setup test data
        SetupTestData(persistence);

        var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

        return builder
            .UseMonolithMesh()
            .ConfigureServices(services => services
                .AddPersistence(persistence)
                .Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
            .AddJsonGraphConfiguration(testDataDirectory);
    }

    [Fact(Timeout = 30000)]
    public async Task ACME_CreatableTypes_IncludesProjectAndGlobalTypes()
    {
        // Act - ACME is an Organization, should be able to create ACME/Project
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("ACME", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Should include ACME/Project (defined under ACME)
        creatableTypes.Should().Contain(t => t.NodeTypePath == "ACME/Project");
        // Should also include global types
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Markdown");
        creatableTypes.Should().Contain(t => t.NodeTypePath == "NodeType");
    }

    [Fact(Timeout = 30000)]
    public async Task ProductLaunch_CreatableTypes_IncludesTodo()
    {
        // Act - ProductLaunch is an instance of ACME/Project, should be able to create ACME/Project/Todo
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("ACME/ProductLaunch", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        creatableTypes.Should().Contain(t => t.NodeTypePath == "ACME/Project/Todo");
        // Should also include global types
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Markdown");
    }

    /// <summary>
    /// Test that verifies the full creatable types algorithm for a Project instance:
    /// 1. Query by node's path to find types defined directly under the node
    /// 2. Query by node's NodeType to find types for instances of that type
    /// This is the critical test case for the bug where "Add Todo" is not shown
    /// when viewing a Project (like ProductLaunch).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProductLaunch_CreatableTypes_VerifyFullAlgorithm()
    {
        // Arrange - Verify the data setup is correct
        var productLaunchNode = await Persistence.GetNodeAsync("ACME/ProductLaunch", TestContext.Current.CancellationToken);
        productLaunchNode.Should().NotBeNull("ProductLaunch node should exist");
        productLaunchNode!.NodeType.Should().Be("ACME/Project", "ProductLaunch should be of NodeType ACME/Project");

        var projectTypeNode = await Persistence.GetNodeAsync("ACME/Project", TestContext.Current.CancellationToken);
        projectTypeNode.Should().NotBeNull("ACME/Project NodeType should exist");
        projectTypeNode!.NodeType.Should().Be("NodeType", "ACME/Project should be a NodeType");

        var todoTypeNode = await Persistence.GetNodeAsync("ACME/Project/Todo", TestContext.Current.CancellationToken);
        todoTypeNode.Should().NotBeNull("ACME/Project/Todo NodeType should exist");
        todoTypeNode!.NodeType.Should().Be("NodeType", "ACME/Project/Todo should be a NodeType");

        // Act - Get creatable types for ProductLaunch
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("ACME/ProductLaunch", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Should include Todo (from ACME/Project's children) and global types
        Output.WriteLine($"Found {creatableTypes.Count} creatable types for ACME/ProductLaunch:");
        foreach (var t in creatableTypes)
        {
            Output.WriteLine($"  - {t.NodeTypePath}: {t.DisplayName}");
        }

        creatableTypes.Should().Contain(t => t.NodeTypePath == "ACME/Project/Todo",
            "ProductLaunch (instance of ACME/Project) should be able to create ACME/Project/Todo");
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Markdown", "Should include global Markdown type");
        creatableTypes.Should().Contain(t => t.NodeTypePath == "NodeType", "Should include global NodeType type");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNode_ViaRequest_Succeeds()
    {
        // Arrange - Create a new Todo node under ProductLaunch
        var newTodoNode = MeshNode.FromPath("ACME/ProductLaunch/my-todo") with
        {
            Name = "My Todo",
            NodeType = "ACME/Project/Todo",
            Description = "A test todo item",
            IsPersistent = true
        };

        // Act
        await Persistence.SaveNodeAsync(newTodoNode, TestContext.Current.CancellationToken);

        // Assert - Verify the node was created
        var createdNode = await Persistence.GetNodeAsync("ACME/ProductLaunch/my-todo", TestContext.Current.CancellationToken);
        createdNode.Should().NotBeNull();
        createdNode!.Name.Should().Be("My Todo");
        createdNode.NodeType.Should().Be("ACME/Project/Todo");
    }

    [Fact(Timeout = 30000)]
    public async Task CreatableTypes_WithExplicitConfig_OverridesAuto()
    {
        // Arrange - Create a type with explicit CreatableTypes configuration
        var restrictedTypeDef = new NodeTypeDefinition
        {
            Id = "RestrictedProject",
            Namespace = "ACME",
            DisplayName = "Restricted Project",
            Description = "A project with restricted creatable types",
            CreatableTypes = new List<string> { "ACME/Project/Todo" },
            IncludeGlobalTypes = false
        };
        var restrictedTypeNode = MeshNode.FromPath("ACME/RestrictedProject") with
        {
            Name = "Restricted Project",
            NodeType = "NodeType",
            Content = restrictedTypeDef,
            IsPersistent = true
        };
        await Persistence.SaveNodeAsync(restrictedTypeNode, TestContext.Current.CancellationToken);

        // Create an instance of the restricted type
        var restrictedInstance = MeshNode.FromPath("ACME/MyRestrictedProject") with
        {
            Name = "My Restricted Project",
            NodeType = "ACME/RestrictedProject",
            IsPersistent = true
        };
        await Persistence.SaveNodeAsync(restrictedInstance, TestContext.Current.CancellationToken);

        // Act
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("ACME/MyRestrictedProject", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Should only include explicitly configured types
        creatableTypes.Should().HaveCount(1);
        creatableTypes.Should().Contain(t => t.NodeTypePath == "ACME/Project/Todo");
        creatableTypes.Should().NotContain(t => t.NodeTypePath == "Markdown");
        creatableTypes.Should().NotContain(t => t.NodeTypePath == "NodeType");
    }

    [Fact(Timeout = 30000)]
    public async Task CreatableTypes_SortedByDisplayOrder()
    {
        // Act
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("ACME", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Global types should be at the end (display order 1000 and 1001)
        var lastTwo = creatableTypes.TakeLast(2).ToList();
        lastTwo.Should().Contain(t => t.NodeTypePath == "Markdown" || t.NodeTypePath == "NodeType");
    }

    /// <summary>
    /// Test that creating a node via IMeshCatalog.CreateNodeAsync and then requesting Edit view works.
    /// This tests the exact GUI flow:
    /// 1. User fills in create form
    /// 2. CreateNodeAsync is called
    /// 3. Redirect to /{nodePath}/Edit
    /// 4. Edit view should load without timeout
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateNode_ViaCatalog_ThenRequestEditView_Succeeds()
    {
        // Arrange - Create a unique node path to avoid conflicts
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"test-markdown-{uniqueId}";
        var nodeAddress = new Address(nodePath);

        Output.WriteLine($"Test: Creating node at {nodePath}");

        // Step 1: Create the node via IMeshCatalog (like the GUI does)
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Markdown Node",
            Description = "A test node for create flow",
            NodeType = "Markdown",
            Icon = "Document",
            IsPersistent = true,
            Content = MarkdownContent.Parse($"# Test Markdown Node\n\nThis is a test.", nodePath)
        };

        Output.WriteLine($"Step 1: Calling Catalog.CreateNodeAsync for {nodePath}");

        try
        {
            var createdNode = await Catalog.CreateNodeAsync(node, "test-user", TestContext.Current.CancellationToken);
            Output.WriteLine($"CreateNodeAsync completed: Path={createdNode.Path}, Name={createdNode.Name}");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"CreateNodeAsync failed: {ex.Message}");
            Output.WriteLine($"Exception type: {ex.GetType().Name}");
            Output.WriteLine($"Stack trace: {ex.StackTrace}");
            throw;
        }

        // Step 2: Verify node exists in persistence
        Output.WriteLine($"Step 2: Verifying node exists in persistence");
        var persistedNode = await Persistence.GetNodeAsync(nodePath, TestContext.Current.CancellationToken);
        persistedNode.Should().NotBeNull($"Node {nodePath} should exist in persistence after CreateNodeAsync");
        Output.WriteLine($"Node exists in persistence: Name={persistedNode!.Name}, NodeType={persistedNode.NodeType}");

        // Step 3: Verify node can be resolved via catalog
        Output.WriteLine($"Step 3: Verifying ResolvePathAsync for {nodePath}");
        var resolution = await Catalog.ResolvePathAsync(nodePath);
        resolution.Should().NotBeNull($"Path {nodePath} should resolve via catalog");
        Output.WriteLine($"Path resolved: Prefix={resolution!.Prefix}, Remainder={resolution.Remainder}");

        // Step 4: Get a client and request the Edit layout
        Output.WriteLine($"Step 4: Getting client and requesting Edit layout");
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();
        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);

        Output.WriteLine($"Requesting Edit layout for {nodeAddress}...");
        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);

        try
        {
            var editLayout = await editStream.Timeout(30.Seconds()).FirstAsync();
            Output.WriteLine($"Edit layout received: ValueKind={editLayout.Value.ValueKind}");

            if (editLayout.Value.ValueKind != JsonValueKind.Undefined && editLayout.Value.ValueKind != JsonValueKind.Null)
            {
                var rawText = editLayout.Value.GetRawText();
                Output.WriteLine($"Edit layout content: {rawText[..Math.Min(500, rawText.Length)]}...");
            }

            editLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Edit layout should not be undefined");
        }
        catch (TimeoutException)
        {
            Output.WriteLine("TIMEOUT: Edit layout request timed out after 30 seconds");
            Output.WriteLine("This suggests the node hub is not properly initialized or not responding");
            throw;
        }

        Output.WriteLine("SUCCESS: CreateNode -> Edit flow completed");

        // Cleanup
        Output.WriteLine($"Cleanup: Deleting test node {nodePath}");
        await Catalog.DeleteNodeAsync(nodePath, ct: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Test that creating a Markdown node via IMeshCatalog and getting the default (Read) view works.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CreateNode_ViaCatalog_ThenRequestDefaultView_Succeeds()
    {
        // Arrange
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"test-markdown-default-{uniqueId}";
        var nodeAddress = new Address(nodePath);

        Output.WriteLine($"Test: Creating node at {nodePath}");

        // Step 1: Create the node via IMeshCatalog
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Markdown Default View",
            NodeType = "Markdown",
            IsPersistent = true,
            Content = MarkdownContent.Parse($"# Test\n\nContent here.", nodePath)
        };

        Output.WriteLine($"Step 1: Calling Catalog.CreateNodeAsync");
        var createdNode = await Catalog.CreateNodeAsync(node, "test-user", TestContext.Current.CancellationToken);
        Output.WriteLine($"Node created: Path={createdNode.Path}");

        // Step 2: Request the default (Read) view
        Output.WriteLine($"Step 2: Requesting default (Read) layout");
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();
        var readReference = new LayoutAreaReference(MarkdownLayoutAreas.ReadArea);

        Output.WriteLine($"Requesting Read layout for {nodeAddress}...");
        var readStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, readReference);

        try
        {
            var readLayout = await readStream.Timeout(30.Seconds()).FirstAsync();
            Output.WriteLine($"Read layout received: ValueKind={readLayout.Value.ValueKind}");
            readLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        }
        catch (TimeoutException)
        {
            Output.WriteLine("TIMEOUT: Read layout request timed out");
            throw;
        }

        Output.WriteLine("SUCCESS: CreateNode -> Read flow completed");

        // Cleanup
        await Catalog.DeleteNodeAsync(nodePath, ct: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Test that verifies the complete GUI create flow:
    /// 1. GetCreatableTypesAsync to get available types
    /// 2. CreateNodeAsync to create the node
    /// 3. Request Edit view (simulates redirect)
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task CompleteCreateFlow_GetTypes_Create_Edit_Succeeds()
    {
        // Step 1: Get creatable types (like the create page does)
        Output.WriteLine("Step 1: Getting creatable types for root");
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("", TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        creatableTypes.Should().NotBeEmpty("Should have creatable types");
        Output.WriteLine($"Found {creatableTypes.Count} creatable types");
        foreach (var t in creatableTypes)
        {
            Output.WriteLine($"  - {t.NodeTypePath}: {t.DisplayName}");
        }

        // Find Markdown type
        var markdownType = creatableTypes.FirstOrDefault(t => t.NodeTypePath == "Markdown");
        markdownType.Should().NotBeNull("Markdown should be creatable");

        // Step 2: Create a node
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodeName = "My Test Document";
        var nodeId = nodeName.Replace(" ", "-").ToLowerInvariant();
        var nodePath = $"{nodeId}-{uniqueId}";

        Output.WriteLine($"Step 2: Creating Markdown node at {nodePath}");

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = nodeName,
            NodeType = markdownType!.NodeTypePath,
            Icon = markdownType.Icon,
            IsPersistent = true,
            Content = MarkdownContent.Parse($"# {nodeName}\n\nCreated via test.", nodePath)
        };

        var createdNode = await Catalog.CreateNodeAsync(node, "test-user", TestContext.Current.CancellationToken);
        Output.WriteLine($"Node created: {createdNode.Path}");

        // Step 3: Request Edit view (simulates redirect to /{nodePath}/Edit)
        Output.WriteLine("Step 3: Requesting Edit view (simulates redirect)");
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();
        var nodeAddress = new Address(nodePath);
        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);

        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);

        try
        {
            var editLayout = await editStream.Timeout(30.Seconds()).FirstAsync();
            Output.WriteLine($"Edit layout received: ValueKind={editLayout.Value.ValueKind}");
            editLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        }
        catch (TimeoutException)
        {
            // On timeout, gather diagnostic info
            Output.WriteLine("TIMEOUT: Edit view request timed out");

            // Check if node is in catalog
            var catalogNode = await Catalog.GetNodeAsync(nodeAddress);
            Output.WriteLine($"Catalog.GetNodeAsync result: {(catalogNode != null ? $"Found: {catalogNode.Path}" : "NULL")}");

            // Check persistence
            var persistenceNode = await Persistence.GetNodeAsync(nodePath, TestContext.Current.CancellationToken);
            Output.WriteLine($"Persistence.GetNodeAsync result: {(persistenceNode != null ? $"Found: {persistenceNode.Path}" : "NULL")}");

            // Check resolution
            var resolution = await Catalog.ResolvePathAsync(nodePath);
            Output.WriteLine($"ResolvePathAsync result: {(resolution != null ? $"Prefix={resolution.Prefix}, Remainder={resolution.Remainder}" : "NULL")}");

            throw;
        }

        Output.WriteLine("SUCCESS: Complete create flow works");

        // Cleanup
        await Catalog.DeleteNodeAsync(nodePath, ct: TestContext.Current.CancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Clean up test directory
        if (_testDirectory != null && Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}

/// <summary>
/// Collection definition for CreatableTypesIntegrationTests.
/// Ensures tests in this collection run serially to avoid test isolation issues.
/// </summary>
[CollectionDefinition("CreatableTypesIntegrationTests", DisableParallelization = true)]
public class CreatableTypesIntegrationTestsCollection
{
}

/// <summary>
/// Integration tests that use the actual FileSystem sample data from samples/Graph/Data.
/// These tests verify the creatable types algorithm works with real data on disk.
/// Uses FileSystemPersistenceService for lazy loading (not InitializeAsync) to match real app behavior.
/// </summary>
public class CreatableTypesFileSystemTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly FileSystemStorageAdapter _storageAdapter;
    private readonly FileSystemPersistenceService _persistence;
    private readonly InMemoryMeshQuery _meshQuery;

    public CreatableTypesFileSystemTest(ITestOutputHelper output)
    {
        _output = output;

        // Find the samples/Graph/Data directory relative to the test assembly
        var assemblyLocation = Path.GetDirectoryName(typeof(CreatableTypesFileSystemTest).Assembly.Location)!;
        var sampleDataPath = Path.GetFullPath(Path.Combine(assemblyLocation, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));

        if (!Directory.Exists(sampleDataPath))
        {
            throw new DirectoryNotFoundException($"Sample data directory not found at: {sampleDataPath}");
        }

        _output.WriteLine($"Using sample data from: {sampleDataPath}");

        _storageAdapter = new FileSystemStorageAdapter(sampleDataPath);
        _persistence = new FileSystemPersistenceService(_storageAdapter);
        _meshQuery = new InMemoryMeshQuery(_persistence);
    }

    public void Dispose()
    {
        // No cleanup needed - we're reading from real sample data
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_VerifyDataStructure()
    {
        // No InitializeAsync needed - FileSystemPersistenceService uses lazy loading

        // Verify expected nodes exist
        var acmeProject = await _persistence.GetNodeAsync("ACME/Project");
        acmeProject.Should().NotBeNull("ACME/Project should exist in sample data");
        _output.WriteLine($"ACME/Project: NodeType={acmeProject?.NodeType}, Content={acmeProject?.Content?.GetType().Name}");

        var acmeProjectTodo = await _persistence.GetNodeAsync("ACME/Project/Todo");
        acmeProjectTodo.Should().NotBeNull("ACME/Project/Todo should exist in sample data");
        _output.WriteLine($"ACME/Project/Todo: NodeType={acmeProjectTodo?.NodeType}, Content={acmeProjectTodo?.Content?.GetType().Name}");

        var productLaunch = await _persistence.GetNodeAsync("ACME/ProductLaunch");
        productLaunch.Should().NotBeNull("ACME/ProductLaunch should exist in sample data");
        _output.WriteLine($"ACME/ProductLaunch: NodeType={productLaunch?.NodeType}, Content={productLaunch?.Content?.GetType().Name}");
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_GetChildrenOfACMEProject_ShouldIncludeTodo()
    {
        // Get children of ACME/Project - uses lazy loading
        var children = await _persistence.GetChildrenAsync("ACME/Project").ToListAsync();

        _output.WriteLine($"Children of ACME/Project ({children.Count} total):");
        foreach (var child in children)
        {
            _output.WriteLine($"  - {child.Path}: NodeType={child.NodeType}");
        }

        // Should include Todo
        children.Should().Contain(c => c.Path == "ACME/Project/Todo",
            "ACME/Project/Todo should be a child of ACME/Project");
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_QueryChildNodeTypes_ShouldFindTodo()
    {
        // This is the exact query used by GetCreatableTypesAsync
        var query = "path:ACME/Project nodeType:NodeType scope:children";
        var results = await _meshQuery.QueryAsync<MeshNode>(query).ToListAsync();

        _output.WriteLine($"Query '{query}' returned {results.Count} results:");
        foreach (var result in results)
        {
            _output.WriteLine($"  - {result.Path}: NodeType={result.NodeType}");
        }

        // Should find ACME/Project/Todo
        results.Should().Contain(r => r.Path == "ACME/Project/Todo",
            "Query should find ACME/Project/Todo as a child NodeType of ACME/Project");
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_ProductLaunch_CreatableTypes_ShouldIncludeTodo()
    {
        // Create the NodeTypeService with the real data
        var nodeTypeService = new NodeTypeServiceTestHelper(_persistence, _meshQuery);

        // Get creatable types for ProductLaunch
        var creatableTypes = await nodeTypeService.GetCreatableTypesAsync("ACME/ProductLaunch").ToListAsync();

        _output.WriteLine($"Creatable types for ACME/ProductLaunch ({creatableTypes.Count} total):");
        foreach (var ct in creatableTypes)
        {
            _output.WriteLine($"  - {ct.NodeTypePath}: {ct.DisplayName}");
        }

        // Should include ACME/Project/Todo
        creatableTypes.Should().Contain(t => t.NodeTypePath == "ACME/Project/Todo",
            "ProductLaunch (instance of ACME/Project) should be able to create ACME/Project/Todo");
    }
}

/// <summary>
/// Minimal NodeTypeService implementation for testing without full MeshHub setup.
/// </summary>
internal class NodeTypeServiceTestHelper
{
    private readonly IPersistenceService _persistence;
    private readonly IMeshQuery _meshQuery;
    private static readonly string[] GlobalTypes = ["Markdown", "NodeType", "Agent"];

    public NodeTypeServiceTestHelper(IPersistenceService persistence, IMeshQuery meshQuery)
    {
        _persistence = persistence;
        _meshQuery = meshQuery;
    }

    public async IAsyncEnumerable<CreatableTypeInfo> GetCreatableTypesAsync(
        string nodePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = new List<CreatableTypeInfo>();
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Get the node to check its type
        var node = await _persistence.GetNodeAsync(nodePath, ct);
        NodeTypeDefinition? typeDef = null;

        // If node has a NodeType, get its definition
        if (node?.NodeType != null)
        {
            var typeNode = await _persistence.GetNodeAsync(node.NodeType, ct);
            typeDef = typeNode?.Content as NodeTypeDefinition;
        }

        // Check for explicit CreatableTypes configuration
        if (typeDef?.CreatableTypes != null && typeDef.CreatableTypes.Count > 0)
        {
            foreach (var typePath in typeDef.CreatableTypes)
            {
                var typeInfo = await GetCreatableTypeInfoAsync(typePath, ct);
                if (typeInfo != null && addedPaths.Add(typeInfo.NodeTypePath))
                {
                    result.Add(typeInfo);
                }
            }
        }
        else
        {
            // Auto-compute from hierarchy
            await AddComputedCreatableTypesAsync(result, addedPaths, node, ct);
        }

        // Add global types
        var includeGlobal = typeDef?.IncludeGlobalTypes ?? true;
        if (includeGlobal)
        {
            foreach (var globalType in GlobalTypes)
            {
                if (!addedPaths.Add(globalType))
                    continue;

                var typeInfo = await GetCreatableTypeInfoAsync(globalType, ct);
                if (typeInfo != null)
                    result.Add(typeInfo);
            }
        }

        foreach (var typeInfo in result.OrderBy(t => t.DisplayOrder).ThenBy(t => t.DisplayName ?? t.NodeTypePath))
        {
            yield return typeInfo;
        }
    }

    private async Task AddComputedCreatableTypesAsync(
        List<CreatableTypeInfo> result,
        HashSet<string> addedPaths,
        MeshNode? node,
        CancellationToken ct)
    {
        if (node == null || string.IsNullOrEmpty(node.Path))
        {
            // Root level - find root NodeTypes
            var rootQuery = "nodeType:NodeType";
            await foreach (var typeNode in _meshQuery.QueryAsync<MeshNode>(rootQuery, null, ct).WithCancellation(ct))
            {
                if (!typeNode.Path.Contains('/') && !GlobalTypes.Contains(typeNode.Path))
                {
                    var typeInfo = CreateCreatableTypeInfoFromNode(typeNode);
                    if (addedPaths.Add(typeInfo.NodeTypePath))
                    {
                        result.Add(typeInfo);
                    }
                }
            }
            return;
        }

        // 1. Query by node's path - find NodeTypes defined directly under this node
        var pathQuery = $"path:{node.Path} nodeType:NodeType scope:children";
        await foreach (var childType in _meshQuery.QueryAsync<MeshNode>(pathQuery, null, ct).WithCancellation(ct))
        {
            var typeInfo = CreateCreatableTypeInfoFromNode(childType);
            if (addedPaths.Add(typeInfo.NodeTypePath))
            {
                result.Add(typeInfo);
            }
        }

        // 2. If node has a NodeType, query by NodeType to find types for instances
        if (node.NodeType != null && node.NodeType != node.Path)
        {
            var typeQuery = $"path:{node.NodeType} nodeType:NodeType scope:children";
            await foreach (var childType in _meshQuery.QueryAsync<MeshNode>(typeQuery, null, ct).WithCancellation(ct))
            {
                var typeInfo = CreateCreatableTypeInfoFromNode(childType);
                if (addedPaths.Add(typeInfo.NodeTypePath))
                {
                    result.Add(typeInfo);
                }
            }
        }
    }

    private async Task<CreatableTypeInfo?> GetCreatableTypeInfoAsync(string nodeTypePath, CancellationToken ct)
    {
        var typeNode = await _persistence.GetNodeAsync(nodeTypePath, ct);
        if (typeNode == null)
            return null;

        return CreateCreatableTypeInfoFromNode(typeNode);
    }

    private static CreatableTypeInfo CreateCreatableTypeInfoFromNode(MeshNode typeNode)
    {
        var typeDef = typeNode.Content as NodeTypeDefinition;
        return new CreatableTypeInfo(
            NodeTypePath: typeNode.Path,
            DisplayName: typeDef?.DisplayName ?? typeNode.Name ?? typeNode.Id,
            Icon: typeNode.Icon ?? typeDef?.Icon,
            Description: typeNode.Description ?? typeDef?.Description,
            DisplayOrder: typeDef?.DisplayOrder ?? typeNode.DisplayOrder ?? 0
        );
    }
}

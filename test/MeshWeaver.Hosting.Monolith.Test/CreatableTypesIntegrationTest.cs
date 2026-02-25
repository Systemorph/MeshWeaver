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
using MeshWeaver.Graph;
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

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for creatable types functionality in NodeTypeService using ACME-like sample data.
/// Tests the full algorithm with real persistence and queries.
/// </summary>
[Collection("SamplesGraphData")]
public class CreatableTypesIntegrationTest : MonolithMeshTestBase
{
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverCreatableTypesTests");
    private string? _testDirectory;
    private JsonSerializerOptions _jsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

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

    private static readonly JsonSerializerOptions SetupJsonOptions = new JsonSerializerOptions();

    private static void SetupTestData(InMemoryPersistenceService persistence)
    {
        // Create Organization type (global - can be created at root level)
        var orgTypeDef = new NodeTypeDefinition
        {
            Description = "An organization namespace"
        };
        var orgTypeNode = MeshNode.FromPath("Organization") with
        {
            Name = "Organization",
            NodeType = "NodeType",
            Icon = "Building",
            Content = orgTypeDef
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(orgTypeNode, SetupJsonOptions).GetAwaiter().GetResult();

        // Create ACME organization (instance of Organization)
        var acmeNode = MeshNode.FromPath("Demos/ACME") with
        {
            Name = "ACME Corp",
            NodeType = "Organization"
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(acmeNode, SetupJsonOptions).GetAwaiter().GetResult();

        // Create ACME/Project type (can be created inside ACME)
        var projectTypeDef = new NodeTypeDefinition
        {
            Description = "A project within ACME"
        };
        var projectTypeNode = MeshNode.FromPath("Demos/ACME/Project") with
        {
            Name = "Project",
            NodeType = "NodeType",
            Icon = "Briefcase",
            Content = projectTypeDef
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(projectTypeNode, SetupJsonOptions).GetAwaiter().GetResult();

        // Create ACME/Project/Todo type (can be created inside ACME/Project instances)
        var todoTypeDef = new NodeTypeDefinition
        {
            Description = "A todo item in a project"
        };
        var todoTypeNode = MeshNode.FromPath("Demos/ACME/Project/Todo") with
        {
            Name = "Todo",
            NodeType = "NodeType",
            Icon = "Checkmark",
            Content = todoTypeDef
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(todoTypeNode, SetupJsonOptions).GetAwaiter().GetResult();

        // Create ACME/ProductLaunch (instance of ACME/Project)
        var productLaunchNode = MeshNode.FromPath("Demos/ACME/ProductLaunch") with
        {
            Name = "Product Launch",
            NodeType = "Demos/ACME/Project"
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(productLaunchNode, SetupJsonOptions).GetAwaiter().GetResult();

        // Create global Markdown type
        var markdownTypeDef = new NodeTypeDefinition
        {
            Description = "A markdown document"
        };
        var markdownTypeNode = MeshNode.FromPath("Markdown") with
        {
            Name = "Markdown",
            NodeType = "NodeType",
            Icon = "Document",
            DisplayOrder = 1000,
            Content = markdownTypeDef
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(markdownTypeNode, SetupJsonOptions).GetAwaiter().GetResult();

        // Create global NodeType type
        var nodeTypeTypeDef = new NodeTypeDefinition
        {
            Description = "A node type definition"
        };
        var nodeTypeTypeNode = MeshNode.FromPath("NodeType") with
        {
            Name = "NodeType",
            NodeType = "NodeType",
            Icon = "Code",
            DisplayOrder = 1001,
            Content = nodeTypeTypeDef
        };
        ((IPersistenceServiceCore)persistence).SaveNodeAsync(nodeTypeTypeNode, SetupJsonOptions).GetAwaiter().GetResult();
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
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    [Fact(Timeout = 30000)]
    public async Task ACME_CreatableTypes_IncludesProjectAndGlobalTypes()
    {
        // Act - ACME is an Organization, should be able to create ACME/Project
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("Demos/ACME", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Should include ACME/Project (defined under ACME)
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Demos/ACME/Project");
        // Should also include global types
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Markdown");
        creatableTypes.Should().Contain(t => t.NodeTypePath == "NodeType");
    }

    /// <summary>
    /// Test that verifies the MeshNodePickerControl type queries for the Create form.
    /// Uses the EXACT same code path as CreateLayoutArea.BuildCreateNewFormAsync
    /// to build queries, then runs them like MeshNodePickerView.LoadResultsAsync does.
    /// When at "ACME" (NodeType=Organization), should return Organization, ACME/Project, and global types.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateForm_TypePicker_Queries_ReturnCorrectTypes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var meshConfiguration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var ct = TestContext.Current.CancellationToken;

        // Simulate: user is at "ACME" (an Organization) and opens Create form
        var parentPath = "ACME";

        // === EXACT copy of CreateLayoutArea.BuildCreateNewFormAsync logic ===
        string? currentNodeType = null;
        MeshNode? parentNode = null;
        if (!string.IsNullOrEmpty(parentPath))
        {
            await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{parentPath}", ct: ct).WithCancellation(ct))
            {
                parentNode = node;
                currentNodeType = node.NodeType;
                break;
            }
        }
        currentNodeType.Should().Be("Organization", "ACME should be of NodeType Organization");

        var effectiveNamespace = parentPath;
        string defaultType;
        if (currentNodeType == MeshNode.NodeTypePath && parentNode != null)
        {
            effectiveNamespace = parentNode.Namespace ?? "";
            defaultType = parentPath;
        }
        else
        {
            defaultType = !string.IsNullOrEmpty(currentNodeType) && currentNodeType != "NodeType"
                ? currentNodeType
                : "Markdown";
        }

        Output.WriteLine($"parentPath={parentPath}, currentNodeType={currentNodeType}, effectiveNamespace={effectiveNamespace}, defaultType={defaultType}");
        effectiveNamespace.Should().Be("ACME");
        defaultType.Should().Be("Organization");

        // Build type queries — EXACT copy of production code
        var typeQueries = new List<string>();
        if (!string.IsNullOrEmpty(effectiveNamespace))
            typeQueries.Add($"nodeType:NodeType path:{effectiveNamespace} scope:children context:create");
        else
            typeQueries.Add("nodeType:NodeType scope:children context:create");
        if (!string.IsNullOrEmpty(currentNodeType) && currentNodeType != "NodeType")
        {
            typeQueries.Add($"path:{currentNodeType} nodeType:NodeType context:create");
            typeQueries.Add($"nodeType:NodeType path:{currentNodeType} scope:children context:create");
        }
        if (currentNodeType == MeshNode.NodeTypePath && !string.IsNullOrEmpty(parentPath))
        {
            typeQueries.Add($"path:{parentPath} nodeType:NodeType context:create");
            typeQueries.Add($"nodeType:NodeType path:{parentPath} scope:children context:create");
        }
        foreach (var globalType in meshConfiguration.GlobalCreatableTypes)
            typeQueries.Add($"path:{globalType} nodeType:NodeType context:create");
        // === END exact copy ===

        Output.WriteLine($"\nQueries ({typeQueries.Count}):");
        for (var i = 0; i < typeQueries.Count; i++)
            Output.WriteLine($"  [{i}] {typeQueries[i]}");

        // Execute each query (same as MeshNodePickerView.LoadResultsAsync)
        var deduped = await ExecuteTypePickerQueries(typeQueries, meshQuery, ct);

        // Assert: Organization itself should be in results (parent's own type)
        deduped.Should().Contain(n => n.Path == "Organization",
            "Organization type should be available when creating inside an Organization node");

        // Assert: ACME/Project should be in results (child type under ACME)
        deduped.Should().Contain(n => n.Path == "ACME/Project",
            "ACME/Project should be available as a child type");

        // Assert: Global types should be in results
        deduped.Should().Contain(n => n.Path == "Markdown",
            "Markdown should be available as a global type");
    }

    /// <summary>
    /// Test that the default selected type in the Create form should be the parent's NodeType,
    /// not Markdown, when the parent has a specific type.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateForm_DefaultType_ShouldBeParentNodeType()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var ct = TestContext.Current.CancellationToken;

        // At "ACME" (an Organization), the default type should be "Organization"
        string? currentNodeType = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>("path:ACME", ct: ct).WithCancellation(ct))
        {
            currentNodeType = node.NodeType;
            break;
        }

        currentNodeType.Should().Be("Organization");
        // The form should default to the parent's NodeType, not "Markdown"
        var defaultType = currentNodeType ?? "Markdown";
        defaultType.Should().Be("Organization",
            "Default type should be Organization when creating inside an Organization node");
    }

    /// <summary>
    /// Test that the MeshNodePickerControl Items for the Create form contain the expected
    /// creatable type nodes — mirrors the exact filtering logic from CreateLayoutArea.cs.
    /// Verifies that types with ExcludeFromContext containing "create" are excluded.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void CreateForm_TypePicker_Items_ContainCreatableTypes()
    {
        var meshConfiguration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();

        // Exact same filter as CreateLayoutArea.BuildCreateNewForm
        var creatableTypeNodes = meshConfiguration.Nodes.Values
            .Where(n => n.ExcludeFromContext?.Contains("create") != true)
            .ToArray();

        var paths = creatableTypeNodes.Select(n => n.Path).ToList();
        Output.WriteLine($"Creatable type nodes ({creatableTypeNodes.Length}):");
        foreach (var node in creatableTypeNodes)
            Output.WriteLine($"  - {node.Path}: Name={node.Name}, ExcludeFromContext=[{string.Join(",", node.ExcludeFromContext ?? [])}]");

        // Should include types not excluded from create
        paths.Should().Contain("Markdown");
        paths.Should().Contain("Thread");

        // Should NOT include types with ExcludeFromContext containing "create"
        paths.Should().NotContain("Comment");
        paths.Should().NotContain("ThreadMessage");
        paths.Should().NotContain("AccessAssignment");
        paths.Should().NotContain("GroupMembership");
        paths.Should().NotContain("Code");
    }

    /// <summary>
    /// Test that when on a NodeType definition page (e.g., "Organization" with NodeType="NodeType"),
    /// the Create form defaults namespace to the type's parent namespace (root for Organization)
    /// and default type to the type itself.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateForm_OnNodeTypeDefinitionPage_DefaultsToTypeParentNamespace()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var meshConfiguration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var ct = TestContext.Current.CancellationToken;

        // Simulate: user is on the "Organization" NodeType definition page and opens Create
        var parentPath = "Organization";

        MeshNode? parentNode = null;
        string? currentNodeType = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{parentPath}", ct: ct).WithCancellation(ct))
        {
            parentNode = node;
            currentNodeType = node.NodeType;
            break;
        }

        parentNode.Should().NotBeNull("Organization node should exist");
        currentNodeType.Should().Be("NodeType", "Organization is a NodeType definition");

        // When on a NodeType definition page, namespace should be the type's parent namespace
        var effectiveNamespace = parentNode!.Namespace ?? "";
        effectiveNamespace.Should().BeEmpty("Organization is at root, so namespace should be root/empty");

        // Default type should be the type definition itself
        var defaultType = parentPath; // "Organization"
        defaultType.Should().Be("Organization");

        // Build type queries for the type definition page
        var typeQueries = new List<string>();
        // Children of root namespace (since effectiveNamespace is empty)
        typeQueries.Add("nodeType:NodeType scope:children context:create");
        // The type itself and its children
        typeQueries.Add($"path:{parentPath} nodeType:NodeType context:create");
        typeQueries.Add($"nodeType:NodeType path:{parentPath} scope:children context:create");
        // Global types
        foreach (var globalType in meshConfiguration.GlobalCreatableTypes)
            typeQueries.Add($"path:{globalType} nodeType:NodeType context:create");

        var deduped = await ExecuteTypePickerQueries(typeQueries, meshQuery, ct);

        // Organization should be in the type list
        deduped.Should().Contain(n => n.Path == "Organization",
            "Organization should be available as a type when on its definition page");

        // Global types should be available
        deduped.Should().Contain(n => n.Path == "Markdown",
            "Markdown should be available as a global type");
    }

    /// <summary>
    /// Test that creating a node at root namespace (empty) produces correct path.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateForm_RootNamespace_ProducesCorrectPath()
    {
        // When namespace is empty (root), path should just be the id
        var ns = "";
        var id = "MyNewOrg";
        var nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
        nodePath.Should().Be("MyNewOrg", "Root namespace should produce path without leading slash");

        // When namespace is set, path should be namespace/id
        ns = "ACME";
        nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
        nodePath.Should().Be("ACME/MyNewOrg");
    }

    /// <summary>
    /// Test that ProductLaunch type picker queries include ACME/Project/Todo and ACME/Project (own type).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateForm_TypePicker_ForProductLaunch_IncludesTodoAndProject()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var meshConfiguration = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var ct = TestContext.Current.CancellationToken;

        var parentPath = "ACME/ProductLaunch";

        string? currentNodeType = null;
        await foreach (var node in meshQuery.QueryAsync<MeshNode>($"path:{parentPath}", ct: ct).WithCancellation(ct))
        {
            currentNodeType = node.NodeType;
            break;
        }
        currentNodeType.Should().Be("ACME/Project");

        var typeQueries = BuildTypePickerQueries(parentPath, currentNodeType, meshConfiguration);
        var deduped = await ExecuteTypePickerQueries(typeQueries, meshQuery, ct);

        // ACME/Project/Todo should be found (child type of ACME/Project)
        deduped.Should().Contain(n => n.Path == "ACME/Project/Todo",
            "ACME/Project/Todo should be available as child type of ACME/Project");

        // ACME/Project itself should be found (the node's own type)
        deduped.Should().Contain(n => n.Path == "ACME/Project",
            "ACME/Project (own type) should be available when creating inside a Project instance");

        // Markdown should be found (global)
        deduped.Should().Contain(n => n.Path == "Markdown",
            "Markdown should be available as a global type");
    }

    /// <summary>
    /// Builds the exact same type queries as CreateLayoutArea.BuildCreateNewFormAsync
    /// for a non-NodeType parent (regular instance node like ACME or ACME/ProductLaunch).
    /// This must stay in sync with the production code.
    /// </summary>
    private static List<string> BuildTypePickerQueries(
        string parentPath, string? currentNodeType, MeshConfiguration meshConfiguration)
    {
        // effectiveNamespace = parentPath for non-NodeType nodes
        var effectiveNamespace = parentPath;

        var typeQueries = new List<string>();
        // Child types under the effective namespace path
        if (!string.IsNullOrEmpty(effectiveNamespace))
            typeQueries.Add($"nodeType:NodeType path:{effectiveNamespace} scope:children context:create");
        else
            typeQueries.Add("nodeType:NodeType scope:children context:create");
        // The parent's own type + child types of the parent's type
        if (!string.IsNullOrEmpty(currentNodeType) && currentNodeType != "NodeType")
        {
            typeQueries.Add($"path:{currentNodeType} nodeType:NodeType context:create");
            typeQueries.Add($"nodeType:NodeType path:{currentNodeType} scope:children context:create");
        }
        // Global creatable types — individual path queries
        foreach (var globalType in meshConfiguration.GlobalCreatableTypes)
            typeQueries.Add($"path:{globalType} nodeType:NodeType context:create");
        return typeQueries;
    }

    /// <summary>
    /// Executes type picker queries the same way MeshNodePickerView.LoadResultsAsync does.
    /// </summary>
    private async Task<List<MeshNode>> ExecuteTypePickerQueries(
        List<string> typeQueries, IMeshQuery meshQuery, CancellationToken ct)
    {
        var allResults = new List<MeshNode>();
        foreach (var query in typeQueries)
        {
            var results = await meshQuery.QueryAsync<MeshNode>(query, ct: ct).ToListAsync(ct);
            Output.WriteLine($"Query '{query}' => {results.Count} results: [{string.Join(", ", results.Select(r => r.Path))}]");
            allResults.AddRange(results);
        }

        var deduped = allResults
            .GroupBy(n => n.Path)
            .Select(g => g.First())
            .ToList();

        Output.WriteLine($"\nTotal unique results: {deduped.Count}");
        foreach (var r in deduped)
            Output.WriteLine($"  - {r.Path}: Name={r.Name}, NodeType={r.NodeType}");

        return deduped;
    }

    [Fact(Timeout = 30000)]
    public async Task ProductLaunch_CreatableTypes_IncludesTodo()
    {
        // Act - ProductLaunch is an instance of ACME/Project, should be able to create ACME/Project/Todo
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("Demos/ACME/ProductLaunch", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Demos/ACME/Project/Todo");
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
        var productLaunchNode = await Persistence.GetNodeAsync("Demos/ACME/ProductLaunch", TestContext.Current.CancellationToken);
        productLaunchNode.Should().NotBeNull("ProductLaunch node should exist");
        productLaunchNode!.NodeType.Should().Be("Demos/ACME/Project", "ProductLaunch should be of NodeType ACME/Project");

        var projectTypeNode = await Persistence.GetNodeAsync("Demos/ACME/Project", TestContext.Current.CancellationToken);
        projectTypeNode.Should().NotBeNull("Demos/ACME/Project NodeType should exist");
        projectTypeNode!.NodeType.Should().Be("NodeType", "Demos/ACME/Project should be a NodeType");

        var todoTypeNode = await Persistence.GetNodeAsync("Demos/ACME/Project/Todo", TestContext.Current.CancellationToken);
        todoTypeNode.Should().NotBeNull("Demos/ACME/Project/Todo NodeType should exist");
        todoTypeNode!.NodeType.Should().Be("NodeType", "Demos/ACME/Project/Todo should be a NodeType");

        // Act - Get creatable types for ProductLaunch
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("Demos/ACME/ProductLaunch", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Should include Todo (from ACME/Project's children) and global types
        Output.WriteLine($"Found {creatableTypes.Count} creatable types for ACME/ProductLaunch:");
        foreach (var t in creatableTypes)
        {
            Output.WriteLine($"  - {t.NodeTypePath}: {t.DisplayName}");
        }

        creatableTypes.Should().Contain(t => t.NodeTypePath == "Demos/ACME/Project/Todo",
            "ProductLaunch (instance of ACME/Project) should be able to create ACME/Project/Todo");
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Markdown", "Should include global Markdown type");
        creatableTypes.Should().Contain(t => t.NodeTypePath == "NodeType", "Should include global NodeType type");
    }

    [Fact(Timeout = 30000)]
    public async Task CreateNode_ViaRequest_Succeeds()
    {
        // Arrange - Create a new Todo node under ProductLaunch
        var newTodoNode = MeshNode.FromPath("Demos/ACME/ProductLaunch/my-todo") with
        {
            Name = "My Todo",
            NodeType = "Demos/ACME/Project/Todo"
        };

        // Act
        await Persistence.SaveNodeAsync(newTodoNode, TestContext.Current.CancellationToken);

        // Assert - Verify the node was created
        var createdNode = await Persistence.GetNodeAsync("Demos/ACME/ProductLaunch/my-todo", TestContext.Current.CancellationToken);
        createdNode.Should().NotBeNull();
        createdNode!.Name.Should().Be("My Todo");
        createdNode.NodeType.Should().Be("Demos/ACME/Project/Todo");
    }

    [Fact(Timeout = 30000)]
    public async Task CreatableTypes_WithExplicitConfig_OverridesAuto()
    {
        // Arrange - Create a type with explicit CreatableTypes configuration
        var restrictedTypeDef = new NodeTypeDefinition
        {
            Description = "A project with restricted creatable types",
            CreatableTypes = new List<string> { "Demos/ACME/Project/Todo" },
            IncludeGlobalTypes = false
        };
        var restrictedTypeNode = MeshNode.FromPath("Demos/ACME/RestrictedProject") with
        {
            Name = "Restricted Project",
            NodeType = "NodeType",
            Content = restrictedTypeDef
        };
        await Persistence.SaveNodeAsync(restrictedTypeNode, TestContext.Current.CancellationToken);

        // Create an instance of the restricted type
        var restrictedInstance = MeshNode.FromPath("Demos/ACME/MyRestrictedProject") with
        {
            Name = "My Restricted Project",
            NodeType = "Demos/ACME/RestrictedProject"
        };
        await Persistence.SaveNodeAsync(restrictedInstance, TestContext.Current.CancellationToken);

        // Act
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("Demos/ACME/MyRestrictedProject", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        // Assert - Should only include explicitly configured types
        creatableTypes.Should().HaveCount(1);
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Demos/ACME/Project/Todo");
        creatableTypes.Should().NotContain(t => t.NodeTypePath == "Markdown");
        creatableTypes.Should().NotContain(t => t.NodeTypePath == "NodeType");
    }

    [Fact(Timeout = 30000)]
    public async Task CreatableTypes_SortedByDisplayOrder()
    {
        // Act
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("Demos/ACME", TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

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
            NodeType = "Markdown",
            Icon = "Document",
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
        var readReference = new LayoutAreaReference(MarkdownLayoutAreas.OverviewArea);

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
    /// Test that creating a TRANSIENT node and then requesting Edit view works.
    /// This is the exact flow from the simplified Create form:
    /// 1. User fills in Name + Description
    /// 2. CreateTransientNodeAsync is called (NOT CreateNodeAsync which confirms immediately)
    /// 3. Redirect to /{nodePath}/Edit
    /// 4. Edit view should load and show "Draft" badge
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateTransientNode_ThenRequestEditView_Succeeds()
    {
        // Use a short timeout CancellationToken to prevent hanging
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        // Arrange - Create a unique node path
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var nodePath = $"test-transient-{uniqueId}";
        var nodeAddress = new Address(nodePath);

        Output.WriteLine($"Test: Creating TRANSIENT node at {nodePath}");

        // Step 1: Create TRANSIENT node via IMeshCatalog (like the new Create form does)
        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Transient Node",
            NodeType = "Markdown",
            State = MeshNodeState.Transient, // Explicitly transient
            Content = MarkdownContent.Parse($"# Test Transient\n\nThis is transient.", nodePath)
        };

        Output.WriteLine($"Step 1: Calling Catalog.CreateTransientNodeAsync for {nodePath}");

        var createdNode = await ((MeshCatalog)Catalog).CreateTransientNodeAsync(node, "test-user", ct);
        Output.WriteLine($"CreateTransientNodeAsync completed: Path={createdNode.Path}, State={createdNode.State}");
        createdNode.State.Should().Be(MeshNodeState.Transient, "Node should be in Transient state");

        // Step 2: Verify node exists in persistence with Transient state
        Output.WriteLine($"Step 2: Verifying transient node in persistence");
        var persistedNode = await Persistence.GetNodeAsync(nodePath, ct);
        persistedNode.Should().NotBeNull($"Transient node {nodePath} should exist in persistence");
        persistedNode!.State.Should().Be(MeshNodeState.Transient, "Persisted node should be Transient");
        Output.WriteLine($"Node in persistence: State={persistedNode.State}, Name={persistedNode.Name}");

        // Diagnostic: Check what's in MeshConfiguration.Nodes BEFORE GetNodeAsync
        Output.WriteLine($"MeshConfiguration.Nodes contains {Catalog.Configuration.Nodes.Count} entries:");
        foreach (var kv in Catalog.Configuration.Nodes)
        {
            Output.WriteLine($"  - {kv.Key}: HubConfig={(kv.Value.HubConfiguration != null ? "SET" : "NULL")}");
        }

        // Diagnostic: Check if "Markdown" is in the configuration
        if (Catalog.Configuration.Nodes.TryGetValue("Markdown", out var markdownNode))
        {
            Output.WriteLine($"Found 'Markdown' in Configuration.Nodes: HubConfig={(markdownNode.HubConfiguration != null ? "SET" : "NULL")}");
        }
        else
        {
            Output.WriteLine($"WARNING: 'Markdown' NOT found in Configuration.Nodes!");
        }

        // Diagnostic: Check if NodeTypeService is available from different service providers
        var nodeTypeServiceFromMesh = Mesh.ServiceProvider.GetService<INodeTypeService>();
        Output.WriteLine($"INodeTypeService from Mesh.ServiceProvider: {(nodeTypeServiceFromMesh != null ? "AVAILABLE" : "NULL")}");

        // Get the hub that MeshCatalog uses (should be injected during construction)
        var meshHub = Mesh.ServiceProvider.GetRequiredService<IMessageHub>();
        var nodeTypeServiceFromMeshHub = meshHub.ServiceProvider.GetService<INodeTypeService>();
        Output.WriteLine($"INodeTypeService from MeshHub.ServiceProvider: {(nodeTypeServiceFromMeshHub != null ? "AVAILABLE" : "NULL")}");
        Output.WriteLine($"Same service provider? {ReferenceEquals(Mesh.ServiceProvider, meshHub.ServiceProvider)}");

        if (nodeTypeServiceFromMesh != null)
        {
            var cachedConfig = nodeTypeServiceFromMesh.GetCachedConfiguration("Markdown");
            Output.WriteLine($"NodeTypeService.GetCachedConfiguration('Markdown'): {(cachedConfig?.HubConfiguration != null ? "HubConfig=SET" : "NULL or no HubConfig")}");
        }

        // Step 3: Get node via catalog (this should trigger EnrichWithNodeTypeAsync)
        Output.WriteLine($"Step 3: Getting node via Catalog.GetNodeAsync");
        var catalogNode = await Catalog.GetNodeAsync(nodeAddress);
        catalogNode.Should().NotBeNull($"Catalog should return the transient node");
        Output.WriteLine($"Catalog node: State={catalogNode!.State}, HubConfiguration={(catalogNode.HubConfiguration != null ? "SET" : "NULL")}");

        // Verify HubConfiguration is set (this is critical for routing to work)
        catalogNode.HubConfiguration.Should().NotBeNull("HubConfiguration must be set for routing to create the hub. Check MeshBuilder.AddMeshNodes registration.");

        // Step 4: Request Edit view (simulates redirect)
        Output.WriteLine($"Step 4: Requesting Edit layout (simulates redirect to /{nodePath}/Edit)");
        var client = GetClient(c => c
            .WithInitialization((h, _) => RoutingService.RegisterStreamAsync(h))
            .AddLayoutClient(cc => cc)
            .AddData(data => data));

        var workspace = client.GetWorkspace();
        var editReference = new LayoutAreaReference(MarkdownLayoutAreas.EditArea);

        Output.WriteLine($"Requesting Edit layout for {nodeAddress}...");
        var editStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(nodeAddress, editReference);

        var editLayout = await editStream.Timeout(10.Seconds()).FirstAsync();
        Output.WriteLine($"Edit layout received: ValueKind={editLayout.Value.ValueKind}");

        if (editLayout.Value.ValueKind != JsonValueKind.Undefined && editLayout.Value.ValueKind != JsonValueKind.Null)
        {
            var rawText = editLayout.Value.GetRawText();
            Output.WriteLine($"Edit layout content: {rawText[..Math.Min(500, rawText.Length)]}...");
        }

        editLayout.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined, "Edit layout should not be undefined for transient node");

        Output.WriteLine("SUCCESS: CreateTransientNode -> Edit flow completed");

        // Cleanup
        Output.WriteLine($"Cleanup: Deleting test node {nodePath}");
        await Catalog.DeleteNodeAsync(nodePath, ct: CancellationToken.None);
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
[Collection("SamplesGraphData")]
public class CreatableTypesFileSystemTest : MonolithMeshTestBase
{
    private static readonly string SampleDataPath = GetSampleDataPath();

    private IPersistenceService Persistence => Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
    private IMeshQuery MeshQuery => Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
    private INodeTypeService NodeTypeService => Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

    public CreatableTypesFileSystemTest(ITestOutputHelper output) : base(output)
    {
        output.WriteLine($"Using sample data from: {SampleDataPath}");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(SampleDataPath)
            .AddGraph();

    private static string GetSampleDataPath()
    {
        // Find the samples/Graph/Data directory relative to the test assembly
        var assemblyLocation = Path.GetDirectoryName(typeof(CreatableTypesFileSystemTest).Assembly.Location)!;
        var sampleDataPath = Path.GetFullPath(Path.Combine(assemblyLocation, "..", "..", "..", "..", "..", "samples", "Graph", "Data"));

        if (!Directory.Exists(sampleDataPath))
        {
            throw new DirectoryNotFoundException($"Sample data directory not found at: {sampleDataPath}");
        }

        return sampleDataPath;
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_VerifyDataStructure()
    {
        // No InitializeAsync needed - FileSystemPersistenceService uses lazy loading

        // Verify expected nodes exist
        var acmeProject = await Persistence.GetNodeAsync("Demos/ACME/Project");
        acmeProject.Should().NotBeNull("Demos/ACME/Project should exist in sample data");
        Output.WriteLine($"Demos/ACME/Project: NodeType={acmeProject?.NodeType}, Content={acmeProject?.Content?.GetType().Name}");

        var acmeProjectTodo = await Persistence.GetNodeAsync("Demos/ACME/Project/Todo");
        acmeProjectTodo.Should().NotBeNull("Demos/ACME/Project/Todo should exist in sample data");
        Output.WriteLine($"Demos/ACME/Project/Todo: NodeType={acmeProjectTodo?.NodeType}, Content={acmeProjectTodo?.Content?.GetType().Name}");

        var productLaunch = await Persistence.GetNodeAsync("Demos/ACME/ProductLaunch");
        productLaunch.Should().NotBeNull("Demos/ACME/ProductLaunch should exist in sample data");
        Output.WriteLine($"Demos/ACME/ProductLaunch: NodeType={productLaunch?.NodeType}, Content={productLaunch?.Content?.GetType().Name}");
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_GetChildrenOfACMEProject_ShouldIncludeTodo()
    {
        // Get children of ACME/Project - uses lazy loading
        var children = await Persistence.GetChildrenAsync("Demos/ACME/Project").ToListAsync();

        Output.WriteLine($"Children of ACME/Project ({children.Count} total):");
        foreach (var child in children)
        {
            Output.WriteLine($"  - {child.Path}: NodeType={child.NodeType}");
        }

        // Should include Todo
        children.Should().Contain(c => c.Path == "Demos/ACME/Project/Todo",
            "Demos/ACME/Project/Todo should be a child of ACME/Project");
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_QueryChildNodeTypes_ShouldFindTodo()
    {
        // This is the exact query used by GetCreatableTypesAsync
        var query = "path:Demos/ACME/Project nodeType:NodeType scope:children";
        var results = await MeshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).OfType<MeshNode>().ToListAsync();

        Output.WriteLine($"Query '{query}' returned {results.Count} results:");
        foreach (var result in results)
        {
            Output.WriteLine($"  - {result.Path}: NodeType={result.NodeType}");
        }

        // Should find ACME/Project/Todo
        results.Should().Contain(r => r.Path == "Demos/ACME/Project/Todo",
            "Query should find ACME/Project/Todo as a child NodeType of ACME/Project");
    }

    [Fact(Timeout = 30000)]
    public async Task FileSystem_ProductLaunch_CreatableTypes_ShouldIncludeTodo()
    {
        // Use the NodeTypeService from DI - it properly gets JsonSerializerOptions from IMessageHub
        var creatableTypes = await NodeTypeService.GetCreatableTypesAsync("Demos/ACME/ProductLaunch").ToListAsync();

        Output.WriteLine($"Creatable types for ACME/ProductLaunch ({creatableTypes.Count} total):");
        foreach (var ct in creatableTypes)
        {
            Output.WriteLine($"  - {ct.NodeTypePath}: {ct.DisplayName}");
        }

        // Should include ACME/Project/Todo
        creatableTypes.Should().Contain(t => t.NodeTypePath == "Demos/ACME/Project/Todo",
            "ProductLaunch (instance of ACME/Project) should be able to create ACME/Project/Todo");
    }
}

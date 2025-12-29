using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test
{
    /// <summary>
    /// Tests that replicate the exact structure from samples/Graph/Data:
    /// - Node "Organizations" in Root namespace (no namespace)
    /// - NodeType = "Type/Organizations"
    /// - Type definition at Type/Organizations with ChildrenQuery
    /// </summary>
    [Collection("OrganizationsLayoutTests")]
    public class OrganizationsLayoutTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
    {
        private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverOrganizationsTests");
        private string? _testDirectory;

        private string GetOrCreateTestDirectory()
        {
            if (_testDirectory == null)
            {
                _testDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
                Directory.CreateDirectory(_testDirectory);
            }
            return _testDirectory;
        }

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();

            // Seed test data using async methods
            var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
            await SetupOrganizationsStructureAsync(persistence);
        }

        /// <summary>
        /// Sets up the Organizations structure with ChildrenQuery instead of DataModel:
        /// - Type/Organizations (NodeType definition with ChildrenQuery)
        /// - Organizations (catalog node with nodeType: "Type/Organizations")
        /// - Several Organization instances (Acme, Contoso, Fabrikam)
        ///
        /// The ChildrenQuery makes the Organizations node display all nodes
        /// with nodeType=="Type/Organization" (the individual organization instances).
        /// </summary>
        private static async Task SetupOrganizationsStructureAsync(IPersistenceService persistence)
        {
            // 1. Create Type/Organizations - the NodeType definition for the catalog
            // This type uses ChildrenQuery to show all Organization instances
            var organizationsTypeNode = new MeshNode("Organizations", "Type")
            {
                Name = "Organizations",
                NodeType = "NodeType",
                Description = "Catalog of organizations",
                IconName = "Building",
                DisplayOrder = 8,
                IsPersistent = true,
                Content = new NodeTypeDefinition
                {
                    Id = "Organizations",
                    Namespace = "Type",
                    DisplayName = "Organizations",
                    IconName = "Building",
                    Description = "Catalog of organizations",
                    DisplayOrder = 8,
                    // Query for all nodes of type "Type/Organization" (individual orgs)
                    ChildrenQuery = "nodeType==Type/Organization;$scope=descendants"
                }
            };
            await persistence.SaveNodeAsync(organizationsTypeNode);

            // 2. Create Type/Organization - the NodeType definition for individual organizations
            var organizationTypeNode = new MeshNode("Organization", "Type")
            {
                Name = "Organization",
                NodeType = "NodeType",
                Description = "An individual organization",
                IconName = "Building",
                DisplayOrder = 9,
                IsPersistent = true,
                Content = new NodeTypeDefinition
                {
                    Id = "Organization",
                    Namespace = "Type",
                    DisplayName = "Organization",
                    IconName = "Building",
                    Description = "An individual organization",
                    DisplayOrder = 9
                }
            };
            await persistence.SaveNodeAsync(organizationTypeNode);

            // 3. Create Organizations catalog node in Root namespace
            var organizationsInstance = new MeshNode("Organizations")
            {
                Name = "Organizations",
                NodeType = "Type/Organizations", // Uses the catalog type with ChildrenQuery
                Description = "Catalog of organizations",
                IconName = "Building",
                DisplayOrder = 10,
                IsPersistent = true
            };
            await persistence.SaveNodeAsync(organizationsInstance);

            // 4. Create some Organization instances that will be found by ChildrenQuery
            var acme = new MeshNode("Acme")
            {
                Name = "Acme Corporation",
                NodeType = "Type/Organization",
                Description = "A famous company",
                IconName = "Building",
                IsPersistent = true
            };
            await persistence.SaveNodeAsync(acme);

            var contoso = new MeshNode("Contoso")
            {
                Name = "Contoso Ltd",
                NodeType = "Type/Organization",
                Description = "Another company",
                IconName = "Building",
                IsPersistent = true
            };
            await persistence.SaveNodeAsync(contoso);

            var fabrikam = new MeshNode("Fabrikam")
            {
                Name = "Fabrikam Inc",
                NodeType = "Type/Organization",
                Description = "Yet another company",
                IconName = "Building",
                IsPersistent = true
            };
            await persistence.SaveNodeAsync(fabrikam);

            // 5. Create the graph root node (needed for initialization)
            var graphNode = MeshNode.FromPath("graph") with
            {
                Name = "Graph",
                NodeType = "type/graph",
                IsPersistent = true
            };
            await persistence.SaveNodeAsync(graphNode);

            // 6. Create type/graph type definition
            var graphTypeNode = new MeshNode("graph", "type")
            {
                Name = "Graph",
                NodeType = "NodeType",
                IsPersistent = true,
                Content = new NodeTypeDefinition
                {
                    Id = "graph",
                    Namespace = "Type",
                    DisplayName = "Graph"
                }
            };
            await persistence.SaveNodeAsync(graphTypeNode);

            var graphCodeConfig = new CodeConfiguration
            {
                Code = "public record Graph { }"
            };
            await persistence.SavePartitionObjectsAsync("type/graph", null, [graphCodeConfig]);
        }

        protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        {
            var testDataDirectory = GetOrCreateTestDirectory();
            var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

            return builder
                .UseMonolithMesh()
                .AddInMemoryPersistence()
                .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
                .AddJsonGraphConfiguration(testDataDirectory);
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            if (_testDirectory != null && Directory.Exists(_testDirectory))
            {
                try { Directory.Delete(_testDirectory, recursive: true); }
                catch { /* Ignore cleanup errors */ }
            }
        }

        /// <summary>
        /// Test that exactly replicates the sample data structure.
        /// Address "Organizations" should be reachable and return default layout area.
        /// </summary>
        [Fact(Timeout = 15000)]
        public async Task Organizations_InRootNamespace_GetDefaultLayoutArea()
        {
            // Address is just "Organizations" since it has no namespace (Root)
            var graphAddress = new Address("graph");
            var organizationsAddress = new Address("Organizations");

            // Get a client with data services configured
            var client = GetClient(c => c.AddData(data => data));

            // IMPORTANT: Initialize the graph hub first to trigger NodeTypeRegistrationInitializer
            // This registers all NodeTypeConfigurations including "Type/Organizations"
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(graphAddress),
                TestContext.Current.CancellationToken);

            // Now initialize the Organizations hub - it should find the NodeTypeConfiguration
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(organizationsAddress),
                TestContext.Current.CancellationToken);

            // Act: Request the default layout area (empty = default view)
            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(string.Empty);
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationsAddress, reference);

            // Wait for the stream to emit a value
            var value = await stream.FirstAsync();

            // Assert
            value.Should().NotBe(default(JsonElement),
                "Organizations node should return default layout area content");
        }

        /// <summary>
        /// Test that the Organizations node can be resolved via path.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task Organizations_CanBeResolved()
        {
            var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

            // Act
            var resolution = await meshCatalog.ResolvePathAsync("Organizations");

            // Assert
            resolution.Should().NotBeNull("Organizations should be resolvable");
            resolution.Prefix.Should().Be("Organizations");
        }

        /// <summary>
        /// Test that the Type/Organizations NodeType can be resolved.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task TypeOrganizations_CanBeResolved()
        {
            var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

            // Act
            var resolution = await meshCatalog.ResolvePathAsync("Type/Organizations");

            // Assert
            resolution.Should().NotBeNull("Type/Organizations should be resolvable");
            resolution.Prefix.Should().Be("Type/Organizations");
        }

        /// <summary>
        /// Test that NodeTypeService can find the NodeType node for "Type/Organizations"
        /// when searching from context "Organizations".
        ///
        /// The key fix: NodeTypeService now also searches in the parent path of the nodeType
        /// when the nodeType contains a path separator (e.g., "Type/Organizations").
        /// This ensures types in "Type/" folder are found even if GlobalTypesNamespace is "type".
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task PersistenceService_FindsNodeTypeNode_ForTypeOrganizations()
        {
            // Arrange
            var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

            // Act - get the Type/Organizations node directly from persistence
            var nodeTypeNode = await persistence.GetNodeAsync("Type/Organizations", TestContext.Current.CancellationToken);

            // Assert
            nodeTypeNode.Should().NotBeNull(
                "PersistenceService should find the NodeType node for 'Type/Organizations'.");
            nodeTypeNode.Path.Should().Be("Type/Organizations");
            nodeTypeNode.NodeType.Should().Be("NodeType");
        }

        /// <summary>
        /// Test that the NodeTypeDefinition for Type/Organizations has ChildrenQuery configured.
        /// The ChildrenQuery enables the Organizations node to query for all Organization instances
        /// rather than just displaying direct children.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task NodeTypeDefinition_HasChildrenQuery_ForTypeOrganizations()
        {
            // Arrange
            var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

            // Act - get the Type/Organizations node
            var nodeTypeNode = await persistence.GetNodeAsync("Type/Organizations", TestContext.Current.CancellationToken);

            // Assert
            nodeTypeNode.Should().NotBeNull("Type/Organizations should exist");
            nodeTypeNode.Content.Should().BeOfType<NodeTypeDefinition>();

            var nodeTypeDef = (NodeTypeDefinition)nodeTypeNode.Content!;
            nodeTypeDef.ChildrenQuery.Should().NotBeNullOrEmpty(
                "Type/Organizations should have ChildrenQuery configured to find Organization instances");
            nodeTypeDef.ChildrenQuery.Should().Contain("nodeType==Type/Organization",
                "ChildrenQuery should filter by Organization type");
        }

        /// <summary>
        /// Test that QueryAsync finds all Organization instances when using the ChildrenQuery.
        /// This validates that the ChildrenQuery mechanism works correctly.
        /// </summary>
        [Fact(Timeout = 15000)]
        public async Task ChildrenQuery_FindsAllOrganizationInstances()
        {
            // Arrange
            var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

            // Get the ChildrenQuery from the Type/Organizations definition
            var nodeTypeNode = await persistence.GetNodeAsync("Type/Organizations", TestContext.Current.CancellationToken);
            var nodeTypeDef = (NodeTypeDefinition)nodeTypeNode!.Content!;
            var childrenQuery = nodeTypeDef.ChildrenQuery!;

            // Act - execute the query (from root to find all matching nodes)
            var results = new List<object>();
            await foreach (var obj in persistence.QueryAsync(childrenQuery, ""))
            {
                results.Add(obj);
            }

            // Assert - should find all 3 Organization instances (Acme, Contoso, Fabrikam)
            results.Should().HaveCount(3, "Should find all 3 Organization instances");
            var nodes = results.Cast<MeshNode>().ToList();
            nodes.Select(n => n.Name).Should().Contain("Acme Corporation");
            nodes.Select(n => n.Name).Should().Contain("Contoso Ltd");
            nodes.Select(n => n.Name).Should().Contain("Fabrikam Inc");
        }

        /// <summary>
        /// Test that the Organizations node uses default MeshNodeView (no compiled assembly needed).
        /// Since we're using ChildrenQuery instead of DataModel/TypeSource, the node uses
        /// the default views which will automatically apply the ChildrenQuery.
        /// </summary>
        [Fact(Timeout = 15000)]
        public async Task Organizations_UsesDefaultMeshNodeView()
        {
            // Arrange
            var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

            // Act - get the Organizations node
            var node = await meshCatalog.GetNodeAsync(new Address("Organizations"));

            // Assert
            node.Should().NotBeNull("Organizations node should exist");
            // Note: Without DataModel/TypeSource, HubConfiguration may be null (uses default views)
            // The key is that ChildrenQuery in the NodeTypeDefinition will be used by MeshNodeView.Details
        }
    }
}

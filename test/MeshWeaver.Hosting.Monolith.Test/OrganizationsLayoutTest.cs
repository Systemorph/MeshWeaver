using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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

namespace MeshWeaver.Hosting.Monolith.Test
{
    /// <summary>
    /// Tests that replicate the exact structure from samples/Graph/Data:
    /// - NodeType "Organization" at root level with NodeType = "NodeType"
    /// - Organization instances (Acme, Contoso) with NodeType = "Organization"
    /// - ChildrenQuery configured to find all instances
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
            await SetupOrganizationsStructureAsync(NodeFactory);
        }

        /// <summary>
        /// Sets up the Organizations structure matching samples/Graph/Data:
        /// - Organization (NodeType definition at root with ChildrenQuery)
        /// - Several Organization instances (Acme, Contoso, Fabrikam) with NodeType = "Organization"
        ///
        /// The ChildrenQuery makes the Organization catalog display all nodes
        /// with nodeType=="Organization".
        /// </summary>
        private static async Task SetupOrganizationsStructureAsync(IMeshService nodeFactory)
        {
            // 1. Create Organization - the NodeType definition at root level
            // This type uses ChildrenQuery to show all Organization instances
            var organizationTypeNode = new MeshNode("Organization")
            {
                Name = "Organization",
                NodeType = "NodeType",
                Icon = "Building",
                Order = 10,
                Content = new NodeTypeDefinition
                {
                    Description = "An organization containing projects",
                    Configuration = "config => config.WithContentType<MeshWeaver.Graph.Dynamic.Organization>().AddNodeTypeView()",
                    // Query for all nodes of type "Organization" ordered by activity
                    ChildrenQuery = "$source=activity;nodeType==Organization;$orderBy=lastAccessedAt:desc;$limit=20"
                }
            };
            // Use Update in case OrganizationNodeProvider already provides a static Organization node
            await nodeFactory.UpdateNodeAsync(organizationTypeNode);

            // 2. Create some Organization instances that will be found by ChildrenQuery
            var acme = new MeshNode("Acme")
            {
                Name = "Acme Corporation",
                NodeType = "Organization",
                Icon = "Building",
                Content = new { Id = "Acme", Name = "Acme Corporation", Description = "A famous company", Logo = "/static/Organization/logos/acme.png" }
            };
            await nodeFactory.CreateNodeAsync(acme);

            var contoso = new MeshNode("Contoso")
            {
                Name = "Contoso Ltd",
                NodeType = "Organization",
                Icon = "Building",
                Content = new { Id = "Contoso", Name = "Contoso Ltd", Description = "Another company", Logo = "/static/Organization/logos/contoso.png" }
            };
            await nodeFactory.CreateNodeAsync(contoso);

            var fabrikam = new MeshNode("Fabrikam")
            {
                Name = "Fabrikam Inc",
                NodeType = "Organization",
                Icon = "Building",
                Content = new { Id = "Fabrikam", Name = "Fabrikam Inc", Description = "Yet another company", Logo = "/static/Organization/logos/fabrikam.png" }
            };
            await nodeFactory.CreateNodeAsync(fabrikam);

            // 3. Create type/graph type definition (must exist before instances)
            var graphTypeNode = new MeshNode("graph", "type")
            {
                Name = "Graph",
                NodeType = "NodeType",
                Content = new NodeTypeDefinition
                {
                    Configuration = "config => config"
                }
            };
            await nodeFactory.CreateNodeAsync(graphTypeNode);

            var graphCodeConfig = new CodeConfiguration
            {
                Code = "public record Graph { }"
            };
            var graphCodeNode = new MeshNode("Graph", "type/graph/_Source")
            {
                NodeType = "Code",
                Name = "Graph",
                Content = graphCodeConfig
            };
            await nodeFactory.CreateNodeAsync(graphCodeNode);

            // 4. Create the graph root node (needed for initialization)
            var graphNode = MeshNode.FromPath("graph") with
            {
                Name = "Graph",
                NodeType = "type/graph"
            };
            await nodeFactory.CreateNodeAsync(graphNode);
        }

        protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        {
            var testDataDirectory = GetOrCreateTestDirectory();
            var cacheDirectory = Path.Combine(testDataDirectory, ".mesh-cache");

            return builder
                .UseMonolithMesh()
                .AddInMemoryPersistence()
                .ConfigureServices(services => services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory))
                .AddGraph();
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
        /// Test that the Organization NodeType node is accessible and returns default layout area.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task Organization_NodeType_GetDefaultLayoutArea()
        {
            // Address is "Organization" at root level
            var graphAddress = new Address("graph");
            var organizationAddress = new Address("Organization");

            // Get a client with data services configured
            var client = GetClient(c => c.AddData(data => data));

            // IMPORTANT: Initialize the graph hub first to trigger NodeTypeRegistrationInitializer
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(graphAddress),
                TestContext.Current.CancellationToken);

            // Now initialize the Organization hub - it should find the NodeTypeConfiguration
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(organizationAddress),
                TestContext.Current.CancellationToken);

            // Act: Request the default layout area (empty = default view)
            var workspace = client.GetWorkspace();
            var reference = new LayoutAreaReference(string.Empty);
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(organizationAddress, reference);

            // Wait for the stream to emit a value
            var value = await stream.Timeout(10.Seconds()).FirstAsync();

            // Assert
            value.Should().NotBe(default(JsonElement),
                "Organization node should return default layout area content");
        }

        /// <summary>
        /// Test that the Organization NodeType can be resolved via path.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task Organization_CanBeResolved()
        {
            var pathResolver = Mesh.ServiceProvider.GetRequiredService<IPathResolver>();

            // Act
            var resolution = await pathResolver.ResolvePathAsync("Organization");

            // Assert
            resolution.Should().NotBeNull("Organization should be resolvable");
            resolution.Prefix.Should().Be("Organization");
        }

        /// <summary>
        /// Test that NodeTypeService can find the NodeType node for "Organization".
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task PersistenceService_FindsNodeTypeNode_ForOrganization()
        {
            // Act - get the Organization node via query
            var nodeTypeNode = await MeshQuery.QueryAsync<MeshNode>("path:Organization scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            // Assert
            nodeTypeNode.Should().NotBeNull(
                "MeshQuery should find the NodeType node for 'Organization'.");
            nodeTypeNode!.Path.Should().Be("Organization");
            nodeTypeNode.NodeType.Should().Be("NodeType");
        }

        /// <summary>
        /// Test that the NodeTypeDefinition for Organization has ChildrenQuery configured.
        /// The ChildrenQuery enables finding all Organization instances.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task NodeTypeDefinition_HasChildrenQuery_ForOrganization()
        {
            // Act - get the Organization node
            var nodeTypeNode = await MeshQuery.QueryAsync<MeshNode>("path:Organization scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            // Assert
            nodeTypeNode.Should().NotBeNull("Organization should exist");
            nodeTypeNode!.Content.Should().BeOfType<NodeTypeDefinition>();

            var nodeTypeDef = (NodeTypeDefinition)nodeTypeNode.Content!;
            nodeTypeDef.ChildrenQuery.Should().NotBeNullOrEmpty(
                "Organization should have ChildrenQuery configured to find instances");
            nodeTypeDef.ChildrenQuery.Should().Contain("nodeType==Organization",
                "ChildrenQuery should filter by Organization type");
        }

        /// <summary>
        /// Test that the NodeTypeDefinition for Organization has Configuration set.
        /// The Configuration contains the lambda expression for hub configuration.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task NodeTypeDefinition_HasConfiguration_ForOrganization()
        {
            // Act - get the Organization node
            var nodeTypeNode = await MeshQuery.QueryAsync<MeshNode>("path:Organization scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            // Assert
            nodeTypeNode.Should().NotBeNull("Organization should exist");
            var nodeTypeDef = (NodeTypeDefinition)nodeTypeNode!.Content!;
            nodeTypeDef.Configuration.Should().NotBeNullOrEmpty(
                "Organization should have Configuration set");
            nodeTypeDef.Configuration.Should().Contain("WithContentType<",
                "Configuration should include content type setup");
        }

        /// <summary>
        /// Test that QueryAsync finds all Organization instances when using the ChildrenQuery.
        /// This validates that the ChildrenQuery mechanism works correctly.
        /// Note: Activity-based queries require activity records, so we test with a simpler query.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task SimpleQuery_FindsAllOrganizationInstances()
        {
            // Arrange
            var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

            // Use a simple query that doesn't require activity records
            var query = "nodeType:Organization scope:descendants";

            // Act - execute the query (from root to find all matching nodes)
            var nodes = await meshQuery.QueryAsync<MeshNode>(query, ct: TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

            // Assert - should find all 3 Organization instances (Acme, Contoso, Fabrikam)
            nodes.Should().HaveCount(3, "Should find all 3 Organization instances");
            nodes.Select(n => n.Name).Should().Contain("Acme Corporation");
            nodes.Select(n => n.Name).Should().Contain("Contoso Ltd");
            nodes.Select(n => n.Name).Should().Contain("Fabrikam Inc");
        }

        /// <summary>
        /// Test that Organization instances have Logo URLs in their content.
        /// </summary>
        [Fact(Timeout = 10000)]
        public async Task OrganizationInstances_HaveLogoUrls()
        {
            // Act - get an organization instance
            var acmeNode = await MeshQuery.QueryAsync<MeshNode>("path:Acme scope:exact", ct: TestContext.Current.CancellationToken).FirstOrDefaultAsync(TestContext.Current.CancellationToken);

            // Assert
            acmeNode.Should().NotBeNull("Acme should exist");
            acmeNode!.Content.Should().NotBeNull("Acme should have content");

            // Content should contain Logo property
            var contentJson = System.Text.Json.JsonSerializer.Serialize(acmeNode.Content);
            contentJson.Should().Contain("Logo", "Organization content should have Logo property");
            contentJson.Should().Contain("/static/Organization/logos/", "Logo URL should use static path");
        }
    }
}

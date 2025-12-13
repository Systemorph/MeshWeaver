using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Domain;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

public class MonolithMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public async Task PingPong()
    {
        var client = GetClient();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(Mesh.Address)
                , new CancellationTokenSource(10.Seconds()).Token
                );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }


    [Theory]
    [InlineData("HubFactory")]
    [InlineData("Kernel")]
    public async Task HubWorksAfterDisposal(string id)
    {
        var client = GetClient();
        var address = AddressExtensions.CreateAppAddress(id);

        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(address)
                , new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();

        client.Post(new DisposeRequest(), o => o.WithTarget(address));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(address)
                , new CancellationTokenSource(10.Seconds()).Token
            );
        response.Should().NotBeNull();
    }


    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode($"{AddressExtensions.AppType}/HubFactory")
            {
                Name = "HubFactory",
                HubConfiguration = x => x
            })
                .AddMeshNodes(new MeshNode($"{AddressExtensions.AppType}/Kernel")
                {
                    Name = "Kernel",
                    StartupScript = @$"using MeshWeaver.Messaging; Mesh.ServiceProvider.CreateMessageHub(AddressExtensions.CreateAppAddress(""Kernel""))"
                })
            .AddKernel();
}

/// <summary>
/// Tests that MeshBuilder.InstallAssemblies properly collects NodeTypeConfigurations.
/// </summary>
public class MeshBuilderNodeTypeConfigurationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void InstallAssemblies_CollectsNodeTypeConfigurations()
    {
        // The GraphDomainAttribute should register NodeTypeConfigurations for graph, org, project, story
        var configuration = ServiceProvider.GetRequiredService<MeshConfiguration>();

        // Assert NodeTypeConfigurations are populated
        configuration.NodeTypeConfigurations.Should().NotBeEmpty("GraphDomainAttribute defines NodeTypeConfigurations");
        configuration.NodeTypeConfigurations.Should().ContainKey("graph");
        configuration.NodeTypeConfigurations.Should().ContainKey("org");
        configuration.NodeTypeConfigurations.Should().ContainKey("project");
        configuration.NodeTypeConfigurations.Should().ContainKey("story");
    }

    [Fact]
    public void InstallAssemblies_CollectsNodes()
    {
        // The GraphDomainAttribute should register the root "graph" node
        var configuration = ServiceProvider.GetRequiredService<MeshConfiguration>();

        // Assert Nodes are populated
        configuration.Nodes.Should().NotBeEmpty("GraphDomainAttribute defines Nodes");
        configuration.Nodes.Should().ContainKey("graph");
    }

    [Fact]
    public void GetNodeTypeConfiguration_ReturnsConfigForKnownType()
    {
        var configuration = ServiceProvider.GetRequiredService<MeshConfiguration>();

        var orgConfig = configuration.GetNodeTypeConfiguration("org");
        orgConfig.Should().NotBeNull();
        orgConfig!.HubConfiguration.Should().NotBeNull("org NodeType should have HubConfiguration");
    }

    [Fact]
    public void GetNodeTypeConfiguration_ReturnsNullForUnknownType()
    {
        var configuration = ServiceProvider.GetRequiredService<MeshConfiguration>();

        var unknownConfig = configuration.GetNodeTypeConfiguration("unknown");
        unknownConfig.Should().BeNull();
    }

    [Fact]
    public void MeshCatalog_NodeTypeConfigurations_AreAvailable()
    {
        // The MeshCatalog should have access to NodeTypeConfigurations via its Configuration property
        var catalog = ServiceProvider.GetRequiredService<Mesh.Services.IMeshCatalog>();

        catalog.Configuration.NodeTypeConfigurations.Should().NotBeEmpty("Catalog should have access to NodeTypeConfigurations");
        catalog.Configuration.NodeTypeConfigurations.Should().ContainKey("org");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder)
            .InstallAssemblies(typeof(GraphDomainAttribute).Assembly.Location);
}

/// <summary>
/// Tests that IMeshCatalog properly finds graph nodes exactly as the Portal configures it.
/// This test mimics the Portal's ConfigurePortalMesh configuration.
/// </summary>
public class GraphDomainIntegrationTest : MonolithMeshTestBase
{
    // Static field to hold test directory - initialized before base constructor runs
    private static readonly string TestDirectoryBase = Path.Combine(Path.GetTempPath(), "MeshWeaverTests");

    [ThreadStatic]
    private static string? _currentTestDirectory;

    public GraphDomainIntegrationTest(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>
    /// Gets or creates the test directory for this test instance.
    /// Called during ConfigureMesh which runs during base constructor.
    /// </summary>
    private static string GetOrCreateTestDirectory()
    {
        if (_currentTestDirectory == null)
        {
            _currentTestDirectory = Path.Combine(TestDirectoryBase, Guid.NewGuid().ToString());
            Directory.CreateDirectory(_currentTestDirectory);
        }
        return _currentTestDirectory;
    }

    public override async ValueTask DisposeAsync()
    {
        var dir = _currentTestDirectory;
        _currentTestDirectory = null;

        await base.DisposeAsync();

        if (dir != null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task GraphNode_IsFoundFromConfiguration()
    {
        // The root "graph" node is registered via GraphDomainAttribute.Nodes
        var catalog = ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var graphNode = await catalog.GetNodeAsync(new Address("graph"));

        // Assert
        graphNode.Should().NotBeNull("Root 'graph' node should be found from MeshConfiguration.Nodes");
        graphNode!.Name.Should().Be("Graph");
        graphNode.NodeType.Should().Be("graph");
        graphNode.HubConfiguration.Should().NotBeNull("graph node should have HubConfiguration from GraphDomainAttribute");
    }

    [Fact]
    public async Task GraphNode_CanCreateHub_AndRespondToPing()
    {
        // Arrange
        var client = GetClient();
        var graphAddress = new Address("graph");

        // Act - Send a ping to the graph hub
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(graphAddress),
            new CancellationTokenSource(10.Seconds()).Token
        );

        // Assert
        response.Should().NotBeNull("Graph hub should be created and respond to ping");
        response.Message.Should().BeOfType<PingResponse>();
    }

    [Fact]
    public async Task PersistedOrgNode_IsFound_AndHasHubConfiguration()
    {
        // Arrange - Create a persisted org node (simulating file system data)
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        await persistence.SaveNodeAsync(new MeshNode("graph/org1")
        {
            Name = "Organization 1",
            NodeType = "org",
            Description = "First organization"
        });

        var catalog = ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var orgNode = await catalog.GetNodeAsync(new Address("graph/org1"));

        // Assert
        orgNode.Should().NotBeNull("Persisted org node should be found");
        orgNode!.Name.Should().Be("Organization 1");
        orgNode.NodeType.Should().Be("org");
        orgNode.HubConfiguration.Should().NotBeNull("HubConfiguration should come from NodeTypeConfiguration for 'org'");
    }

    [Fact]
    public async Task PersistedOrgNode_CanCreateHub_AndRespondToPing()
    {
        // Arrange - Create a persisted org node
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        await persistence.SaveNodeAsync(new MeshNode("graph/org1")
        {
            Name = "Organization 1",
            NodeType = "org"
        });

        var client = GetClient();
        var orgAddress = new Address("graph/org1");

        // Act - Send a ping to the org hub
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(orgAddress),
            new CancellationTokenSource(10.Seconds()).Token
        );

        // Assert
        response.Should().NotBeNull("Org hub should be created and respond to ping");
        response.Message.Should().BeOfType<PingResponse>();
    }

    [Fact]
    public async Task PersistedProjectNode_IsFound_AndHasHubConfiguration()
    {
        // Arrange
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        await persistence.SaveNodeAsync(new MeshNode("graph/org1/project1")
        {
            Name = "Project 1",
            NodeType = "project"
        });

        var catalog = ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var projectNode = await catalog.GetNodeAsync(new Address("graph/org1/project1"));

        // Assert
        projectNode.Should().NotBeNull("Persisted project node should be found");
        projectNode!.NodeType.Should().Be("project");
        projectNode.HubConfiguration.Should().NotBeNull("HubConfiguration should come from NodeTypeConfiguration for 'project'");
    }

    [Fact]
    public async Task PersistedProjectNode_CanCreateHub_AndRespondToPing()
    {
        // Arrange
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        await persistence.SaveNodeAsync(new MeshNode("graph/org1/project1")
        {
            Name = "Project 1",
            NodeType = "project"
        });

        var client = GetClient();
        var projectAddress = new Address("graph/org1/project1");

        // Act
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projectAddress),
            new CancellationTokenSource(10.Seconds()).Token
        );

        // Assert
        response.Should().NotBeNull("Project hub should be created and respond to ping");
        response.Message.Should().BeOfType<PingResponse>();
    }

    [Fact]
    public async Task PersistedStoryNode_IsFound_AndHasHubConfiguration()
    {
        // Arrange
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        await persistence.SaveNodeAsync(new MeshNode("graph/org1/project1/story1")
        {
            Name = "Story 1",
            NodeType = "story"
        });

        var catalog = ServiceProvider.GetRequiredService<IMeshCatalog>();

        // Act
        var storyNode = await catalog.GetNodeAsync(new Address("graph/org1/project1/story1"));

        // Assert
        storyNode.Should().NotBeNull("Persisted story node should be found");
        storyNode!.NodeType.Should().Be("story");
        storyNode.HubConfiguration.Should().NotBeNull("HubConfiguration should come from NodeTypeConfiguration for 'story'");
    }

    [Fact]
    public async Task PersistedStoryNode_CanCreateHub_AndRespondToPing()
    {
        // Arrange
        var persistence = ServiceProvider.GetRequiredService<IPersistenceService>();
        await persistence.SaveNodeAsync(new MeshNode("graph/org1/project1/story1")
        {
            Name = "Story 1",
            NodeType = "story"
        });

        var client = GetClient();
        var storyAddress = new Address("graph/org1/project1/story1");

        // Act
        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(storyAddress),
            new CancellationTokenSource(10.Seconds()).Token
        );

        // Assert
        response.Should().NotBeNull("Story hub should be created and respond to ping");
        response.Message.Should().BeOfType<PingResponse>();
    }

    [Fact]
    public void NodeTypeConfigurations_AreRegistered()
    {
        // Verify NodeTypeConfigurations are properly loaded from GraphDomainAttribute
        var config = ServiceProvider.GetRequiredService<MeshConfiguration>();

        config.NodeTypeConfigurations.Should().ContainKey("graph");
        config.NodeTypeConfigurations.Should().ContainKey("org");
        config.NodeTypeConfigurations.Should().ContainKey("project");
        config.NodeTypeConfigurations.Should().ContainKey("story");

        // Verify each has a HubConfiguration
        config.NodeTypeConfigurations["graph"].HubConfiguration.Should().NotBeNull();
        config.NodeTypeConfigurations["org"].HubConfiguration.Should().NotBeNull();
        config.NodeTypeConfigurations["project"].HubConfiguration.Should().NotBeNull();
        config.NodeTypeConfigurations["story"].HubConfiguration.Should().NotBeNull();
    }

    [Fact]
    public void GraphNode_IsRegisteredInNodes()
    {
        // Verify the root graph node is in MeshConfiguration.Nodes
        var config = ServiceProvider.GetRequiredService<MeshConfiguration>();

        config.Nodes.Should().ContainKey("graph");
        config.Nodes["graph"].Name.Should().Be("Graph");
        config.Nodes["graph"].HubConfiguration.Should().NotBeNull();
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Configure exactly like the Portal does
        return base.ConfigureMesh(builder)
            .AddFileSystemPersistence(GetOrCreateTestDirectory())
            .InstallAssemblies(typeof(GraphDomainAttribute).Assembly.Location);
    }
}

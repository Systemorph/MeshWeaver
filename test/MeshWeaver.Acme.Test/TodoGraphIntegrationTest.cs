using Memex.Portal.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Integration tests for the ProductLaunch project under ACME organization.
/// Tests verify that the JSON-based NodeType configuration works correctly
/// and that task instances can be loaded and accessed.
/// Theme: MeshFlow B2B SaaS product launch campaign.
/// </summary>
public class TodoGraphIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Shared cache directory for all tests - compiled assemblies are reused
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoGraphTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

        Output.WriteLine($"Graph path: {graphPath}");
        Output.WriteLine($"Data directory: {dataDirectory}");
        Output.WriteLine($"Cache directory: {SharedCacheDirectory}");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddAcme()
            .AddOrganizationType()
            .ConfigureServices(services =>
            {
                // Use shared disk cache - first test compiles, subsequent tests reuse
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph();
    }

    /// <summary>
    /// Test that ACME organization hub can be initialized.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ACME_Organization_CanBeInitialized()
    {
        var acmeAddress = new Address("ACME");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(acmeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that Project NodeType under ACME can be initialized.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ACME_Project_NodeType_CanBeInitialized()
    {
        var projectTypeAddress = new Address("ACME/Project");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(projectTypeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that Todo NodeType under Project can be initialized.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ACME_Todo_NodeType_CanBeInitialized()
    {
        var todoTypeAddress = new Address("ACME/Project/Todo");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoTypeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that ProductLaunch project instance can be initialized.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProductLaunch_Instance_CanBeInitialized()
    {
        var productLaunchAddress = new Address("ACME/ProductLaunch");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(productLaunchAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that a task instance can be initialized.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Task_Instance_CanBeInitialized()
    {
        var taskAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(taskAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that ProductLaunch has task children.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProductLaunch_HasTaskChildren()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        var children = await meshQuery.QueryAsync<MeshNode>("namespace:ACME/ProductLaunch/Todo", null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        children.Should().NotBeEmpty("ProductLaunch should have task children");
        children.Should().HaveCountGreaterThan(10, "ProductLaunch should have at least 10 tasks");
        children.Should().Contain(n => n.Path == "ACME/ProductLaunch/Todo/DefinePersona");
    }

    /// <summary>
    /// Test that task instances have correct NodeType.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Task_Instances_HaveCorrectNodeType()
    {
        var task = await MeshQuery.QueryAsync<MeshNode>("path:ACME/ProductLaunch/Todo/DefinePersona").FirstOrDefaultAsync();

        task.Should().NotBeNull();
        task!.NodeType.Should().Be("ACME/Project/Todo");
    }
}

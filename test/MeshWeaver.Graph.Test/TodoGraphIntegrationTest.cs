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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Integration tests for the Todo/Project Graph structure under ACME organization.
/// Tests verify that the JSON-based NodeType configuration works correctly
/// and that Todo instances can be loaded and accessed.
/// </summary>
[Collection("TodoGraphTests")]
public class TodoGraphIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static string GetSamplesGraphPath()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "samples", "Graph");
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = GetSamplesGraphPath();
        var dataDirectory = Path.Combine(graphPath, "Data");
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverTodoGraphTests", Guid.NewGuid().ToString(), ".mesh-cache");
        Directory.CreateDirectory(cacheDirectory);

        Output.WriteLine($"Graph path: {graphPath}");
        Output.WriteLine($"Data directory: {dataDirectory}");
        Output.WriteLine($"Cache directory: {cacheDirectory}");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                // Disable disk caching to avoid file locking issues in tests
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = cacheDirectory;
                    o.EnableDiskCache = false;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddJsonGraphConfiguration(dataDirectory);
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
        var projectTypeAddress = new Address("ACME/Type/Project");

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
        var todoTypeAddress = new Address("ACME/Type/Project/Todo");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoTypeAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that TodoProject instance can be initialized.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TodoProject_Instance_CanBeInitialized()
    {
        var todoProjectAddress = new Address("ACME/TodoProject");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoProjectAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that a Todo instance can be initialized.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Todo_Instance_CanBeInitialized()
    {
        var todoAddress = new Address("ACME/TodoProject/Todo/Todo1");

        var client = GetClient(c => c.AddData(data => data));

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull();
    }

    /// <summary>
    /// Test that TodoProject has Todo children.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TodoProject_HasTodoChildren()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var children = await persistence.GetChildrenAsync("ACME/TodoProject/Todo").ToListAsync(TestContext.Current.CancellationToken);

        children.Should().NotBeEmpty("TodoProject should have Todo children");
        children.Should().HaveCountGreaterThan(10, "TodoProject should have at least 10 todos");
        children.Should().Contain(n => n.Path == "ACME/TodoProject/Todo/Todo1");
    }

    /// <summary>
    /// Test that Todo instances have correct NodeType.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Todo_Instances_HaveCorrectNodeType()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        var todo1 = await persistence.GetNodeAsync("ACME/TodoProject/Todo/Todo1", TestContext.Current.CancellationToken);

        todo1.Should().NotBeNull();
        todo1!.NodeType.Should().Be("ACME/Type/Project/Todo");
    }
}

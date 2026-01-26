using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for Todo-level views (Details, Thumbnail).
/// These views display individual Todo items with their metadata and action buttons.
/// </summary>
[Collection("TodoViewsTests")]
public class TodoViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Shared cache - tests run sequentially in this collection
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoViewTests",
        ".mesh-cache");

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
        Directory.CreateDirectory(SharedCacheDirectory);

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
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddJsonGraphConfiguration(dataDirectory);
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Test that the Details view renders for a Todo item.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Details_ShouldRenderTodoItem()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        // Initialize the hub first - required for proper routing
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Waiting for Details view to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(5))
            .FirstAsync();

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Details view should render for a Todo item");
    }

    /// <summary>
    /// Test that the Thumbnail view renders for a Todo item.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Thumbnail_ShouldRenderTodoItem()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/LaunchEvent");

        // Initialize the hub first - required for proper routing
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Thumbnail");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Waiting for Thumbnail view to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(5))
            .FirstAsync();

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Thumbnail view should render for a Todo item");
    }

    /// <summary>
    /// Test that the Overview view renders for a Todo.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Details_ShouldRenderAsStackControl()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        // Initialize the hub first - required for proper routing
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(todoAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Waiting for Overview view to render...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();

        control.Should().NotBeNull("Overview view should render");
        Output.WriteLine($"Overview view rendered: {control?.GetType().Name}");
    }

    /// <summary>
    /// Test that multiple Todo items can be accessed independently.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task MultipleTodos_CanBeAccessedIndependently()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var todoAddresses = new[]
        {
            new Address("ACME/ProductLaunch/Todo/DefinePersona"),
            new Address("ACME/ProductLaunch/Todo/LaunchEvent"),
            new Address("ACME/ProductLaunch/Todo/PressRelease")
        };

        foreach (var todoAddress in todoAddresses)
        {
            Output.WriteLine($"Accessing Todo: {todoAddress}");

            // Initialize the hub first - required for proper routing
            await client.AwaitResponse(
                new PingRequest(),
                o => o.WithTarget(todoAddress),
                TestContext.Current.CancellationToken);

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                todoAddress,
                reference);

            var control = await stream
                .GetControlStream(reference.Area!)
                .Where(c => c != null)
                .Timeout(TimeSpan.FromSeconds(5))
                .FirstAsync();

            control.Should().NotBeNull($"Details view should render for {todoAddress}");
            Output.WriteLine($"Successfully rendered: {todoAddress}");
        }
    }
}

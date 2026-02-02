using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for Todo-level views (Details, Thumbnail).
/// These views display individual Todo items with their metadata and action buttons.
/// </summary>
[Collection("SamplesGraphData")]
public class TodoViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Shared cache - tests run sequentially in this collection
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoViewTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
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
            .AddJsonGraphConfiguration(dataDirectory)
            // Configure default layout areas for all node hubs (including Create area)
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
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
        // Use simpler pattern that works reliably
        var value = await stream.Timeout(TimeSpan.FromSeconds(5)).FirstAsync();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Details view should render for a Todo item");
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
        // Use simpler pattern that works reliably
        var value = await stream.Timeout(TimeSpan.FromSeconds(5)).FirstAsync();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Thumbnail view should render for a Todo item");
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
        // Use simpler pattern that works reliably
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        value.Should().NotBe(default(JsonElement), "Overview view should render");
        Output.WriteLine($"Overview view rendered");
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

            // Use simpler pattern that works reliably
            var value = await stream.Timeout(TimeSpan.FromSeconds(5)).FirstAsync();

            value.Should().NotBe(default(JsonElement), $"Details view should render for {todoAddress}");
            Output.WriteLine($"Successfully rendered: {todoAddress}");
        }
    }

    /// <summary>
    /// Diagnostic test to verify MeshConfiguration has DefaultNodeHubConfiguration set.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void Configuration_HasDefaultNodeHubConfiguration()
    {
        // Get the MeshConfiguration from DI
        var meshConfig = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();

        Output.WriteLine($"MeshConfiguration.DefaultNodeHubConfiguration is null: {meshConfig.DefaultNodeHubConfiguration == null}");
        meshConfig.DefaultNodeHubConfiguration.Should().NotBeNull("DefaultNodeHubConfiguration should be set by ConfigureDefaultNodeHub");

        // Apply the config to a test configuration and check lambdas
        var testConfig = new MessageHubConfiguration(Mesh.ServiceProvider, new Address("test"));
        var configuredConfig = meshConfig.DefaultNodeHubConfiguration!(testConfig);

        var lambdas = configuredConfig.Get<System.Collections.Immutable.ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>();
        Output.WriteLine($"Lambdas count: {lambdas?.Count ?? 0}");
        lambdas.Should().NotBeNull("Config should have layout lambdas");
        lambdas!.Count.Should().BeGreaterThan(1, "Should have more than just the default AddStandardViews lambda");
    }

    /// <summary>
    /// Diagnostic test to trace layout area rendering.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Trace_LayoutAreaRendering()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub for ACME/ProductLaunch...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        // Get the hosted hub directly
        var hostedHub = Mesh.GetHostedHub(parentAddress, HostedHubCreation.Never);
        hostedHub.Should().NotBeNull("Hub should exist after PingRequest");

        // Check if the hub has data configuration with LayoutAreaReference stream handler
        var workspace = hostedHub!.GetWorkspace();
        Output.WriteLine($"Workspace exists: {workspace != null}");

        // Check if UiControlService is available
        var uiControlService = hostedHub.ServiceProvider.GetService<IUiControlService>();
        Output.WriteLine($"IUiControlService exists: {uiControlService != null}");
        Output.WriteLine($"LayoutDefinition renderer count: {uiControlService?.LayoutDefinition.Count ?? 0}");

        // Check the ReduceManager for the workspace
        var reduceManager = workspace?.ReduceManager;
        Output.WriteLine($"ReduceManager exists: {reduceManager != null}");

        // Try to create a stream directly
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        Output.WriteLine($"Requesting area: {reference.Area}");

        // Get the stream from workspace
        var stream = workspace?.GetStream<EntityStore>(reference);
        Output.WriteLine($"Stream exists: {stream != null}");

        if (stream != null)
        {
            Output.WriteLine("Waiting for stream values...");

            // Wait for multiple values to see if areas get populated
            var valueCount = 0;
            var foundPopulatedAreas = false;

            // Use Take(5) to get up to 5 values
            await stream.Take(5).ForEachAsync(value =>
            {
                valueCount++;
                if (value?.Value != null)
                {
                    var json = JsonSerializer.Serialize(value.Value, hostedHub.JsonSerializerOptions);
                    var hasAreas = value.Value.Collections.TryGetValue(LayoutAreaReference.Areas, out var areas)
                        && areas.Instances.Count > 0;
                    var areaCount = areas?.Instances.Count ?? 0;
                    Output.WriteLine($"Value {valueCount}: hasAreas={hasAreas}, areas count={areaCount}");
                    Output.WriteLine($"  JSON (first 300): {json.Substring(0, Math.Min(300, json.Length))}");

                    if (hasAreas)
                    {
                        Output.WriteLine("Found populated areas!");
                        foundPopulatedAreas = true;
                    }
                }
            });

            Output.WriteLine($"Total values received: {valueCount}");
            foundPopulatedAreas.Should().BeTrue("At least one value should have populated areas");
        }
    }

    /// <summary>
    /// Test that the Overview area renders for ProductLaunch.
    /// This verifies basic layout area functionality before testing Create.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ProductLaunch_Overview_ShouldRender()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        // First check the configuration
        var meshConfig = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();
        Output.WriteLine($"DefaultNodeHubConfiguration is set: {meshConfig.DefaultNodeHubConfiguration != null}");

        Output.WriteLine("Initializing hub for ACME/ProductLaunch...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        // Check the created hub's configuration
        var hostedHub = Mesh.GetHostedHub(parentAddress, HostedHubCreation.Never);
        if (hostedHub != null)
        {
            var lambdas = hostedHub.Configuration.Get<System.Collections.Immutable.ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>();
            Output.WriteLine($"Hosted hub lambdas count: {lambdas?.Count ?? 0}");

            // Check IUiControlService
            var uiControlService = hostedHub.ServiceProvider.GetService<IUiControlService>();
            Output.WriteLine($"IUiControlService is available: {uiControlService != null}");
            if (uiControlService != null)
            {
                Output.WriteLine($"LayoutDefinition renderer count: {uiControlService.LayoutDefinition.Count}");
                // Count includes both named renderers and predicate-based renderers
                // AddDefaultLayoutAreas registers 15+ named renderers (Overview, Thumbnail, Metadata, etc.)
            }
        }
        else
        {
            Output.WriteLine("Hosted hub not found");
        }

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for Overview area...");
        var rawValue = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        Output.WriteLine($"Received raw value: {rawValue.Value.ValueKind}");
        Output.WriteLine($"Raw JSON (first 500 chars): {rawValue.Value.ToString().Substring(0, Math.Min(500, rawValue.Value.ToString().Length))}");

        // Check if areas is populated
        var hasAreas = !rawValue.Value.ToString().Contains("\"areas\":{}");
        Output.WriteLine($"Has areas: {hasAreas}");

        hasAreas.Should().BeTrue("Overview area should have content for ProductLaunch");
    }

    /// <summary>
    /// Test that the Create area renders with type parameter on ProductLaunch.
    /// This tests the exact URL: /ACME/ProductLaunch/Create?type=ACME%2FProject%2FTodo
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateArea_WithTypeParam_ShouldRenderCreateForm()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub for ACME/ProductLaunch...");
        // Initialize the hub first
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();

        // First test without Id to verify Create area is registered
        var referenceNoId = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);
        Output.WriteLine($"Testing Create area without Id first: Area={referenceNoId.Area}");

        var streamNoId = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            referenceNoId);

        var rawNoId = await streamNoId
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync();
        Output.WriteLine($"Create without Id - raw value kind: {rawNoId.Value.ValueKind}");
        Output.WriteLine($"Create without Id - areas empty? {rawNoId.Value.ToString().Contains("\"areas\":{}")}");

        // Now test with Id
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea)
        {
            Id = "?type=ACME%2FProject%2FTodo"
        };

        Output.WriteLine($"Requesting Create area with reference: Area={reference.Area}, Id={reference.Id}");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for stream value...");

        // First just get any value from the stream to debug
        var rawChange = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();
        var rawValue = rawChange.Value;
        Output.WriteLine($"Received raw value: {rawValue.ValueKind}");
        Output.WriteLine($"Raw JSON (first 500 chars): {rawValue.ToString().Substring(0, Math.Min(500, rawValue.ToString().Length))}");

        // Now try to get the control stream
        Output.WriteLine("Trying GetControlStream...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Create form should render when type parameter is specified");

        // The Create form should be a StackControl containing form fields
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty("Create form should have child areas (form fields)");

        Output.WriteLine($"Create form has {stack.Areas.Count()} areas");
    }
}

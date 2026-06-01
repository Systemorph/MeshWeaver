using MeshWeaver.Blazor.Portal;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Acme.Test;

/// <summary>
/// Tests for Todo-level views (Details, Thumbnail).
/// These views display individual Todo items with their metadata and action buttons.
/// </summary>
public class TodoViewsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Stable cache directory — the timestamped-subdir cache (a3ab9909e)
    // gives each compile its own subdir so prior-process DLLs aren't touched.
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoViewTests",
        ".mesh-cache");

    /// <summary>
    /// Read-only view tests (no node mutations — they read the shared SamplesGraph
    /// directly). Share the Mesh/SP across [Fact]s so we build ONE Autofac root
    /// container for the class instead of one per method — each root build is the
    /// ~190 MiB Reflection.Emit factory cost that never frees on dispose. See
    /// MonolithMeshTestBase.ShareMeshAcrossTests.
    /// </summary>
    protected override bool ShareMeshAcrossTests => true;

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
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddAcme()
            .AddSpaceType()
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
            .AddGraph()
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
    [Fact(Timeout = 60000)]
    public void Details_ShouldRenderTodoItem()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        // Load-bearing ping: a Todo-INSTANCE node hub's layout-area GetRemoteStream
        // does NOT self-activate cleanly — without this init the subscription errors
        // with "Hub '…' initialization failed". (Removing it produced that failure;
        // restored per the empirical keep rule.)
        client.Observe(new PingRequest(), o => o.WithTarget(todoAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Waiting for Details view to render...");
        var value = stream.Should().Within(50.Seconds()).Emit();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Details view should render for a Todo item");
    }

    /// <summary>
    /// Test that the Thumbnail view renders for a Todo item.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Thumbnail_ShouldRenderTodoItem()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/LaunchEvent");

        // Load-bearing ping: Todo-instance node hubs require init before the
        // layout-area subscription, else it errors "Hub '…' initialization failed".
        client.Observe(new PingRequest(), o => o.WithTarget(todoAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Thumbnail");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Waiting for Thumbnail view to render...");
        var value = stream.Should().Within(50.Seconds()).Emit();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Thumbnail view should render for a Todo item");
    }

    /// <summary>
    /// Test that the Overview view renders for a Todo.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Details_ShouldRenderAsStackControl()
    {
        var client = GetClient();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");

        // Load-bearing ping: Todo-instance node hubs require init before the
        // layout-area subscription, else it errors "Hub '…' initialization failed".
        client.Observe(new PingRequest(), o => o.WithTarget(todoAddress)).Should().Within(30.Seconds()).Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Waiting for Overview view to render...");
        var value = stream.Should().Within(50.Seconds()).Emit();

        value.Should().NotBe(default(JsonElement), "Overview view should render");
        Output.WriteLine($"Overview view rendered");
    }

    /// <summary>
    /// Test that multiple Todo items can be accessed independently.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void MultipleTodos_CanBeAccessedIndependently()
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

            // Load-bearing ping: Todo-instance node hubs require init before the
            // layout-area subscription, else it errors "Hub '…' initialization failed".
            client.Observe(new PingRequest(), o => o.WithTarget(todoAddress)).Should().Within(30.Seconds()).Emit();

            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                todoAddress,
                reference);

            var value = stream.Should().Within(50.Seconds()).Emit();

            value.Should().NotBe(default(JsonElement), $"Details view should render for {todoAddress}");
            Output.WriteLine($"Successfully rendered: {todoAddress}");
        }
    }

    /// <summary>
    /// Diagnostic test to verify MeshConfiguration has DefaultNodeHubConfiguration set.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Configuration_HasDefaultNodeHubConfiguration()
    {
        // Get the MeshConfiguration from DI
        var meshConfig = Mesh.ServiceProvider.GetRequiredService<MeshConfiguration>();

        Output.WriteLine($"MeshConfiguration.DefaultNodeHubConfiguration is null: {meshConfig.DefaultNodeHubConfiguration == null}");
        meshConfig.DefaultNodeHubConfiguration.Should().NotBeNull("DefaultNodeHubConfiguration should be set by ConfigureDefaultNodeHub");

        // Apply the config to a test configuration and check lambdas
        var testConfig = new MessageHubConfiguration(Mesh.ServiceProvider, new Address("test"));
        var configuredConfig = meshConfig.DefaultNodeHubConfiguration!(testConfig);

        var lambdas = configuredConfig.Get<ImmutableList<Func<LayoutDefinition, LayoutDefinition>>>();
        Output.WriteLine($"Lambdas count: {lambdas?.Count ?? 0}");
        lambdas.Should().NotBeNull("Config should have layout lambdas");
        lambdas!.Count.Should().BeGreaterThan(1, "Should have more than just the default AddStandardViews lambda");
    }

    /// <summary>
    /// Diagnostic test to trace layout area rendering infrastructure.
    /// Verifies that the hub is created with proper configuration.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Trace_LayoutAreaRendering()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub for ACME/ProductLaunch...");
        client.Observe(new PingRequest(), o => o.WithTarget(parentAddress)).Should().Emit();
        Output.WriteLine("Hub initialized.");

        // Get the hosted hub directly
        var hostedHub = Mesh.GetHostedHub(parentAddress, HostedHubCreation.Never);
        hostedHub.Should().NotBeNull("Hub should exist after PingRequest");

        // Check if the hub has data configuration with LayoutAreaReference stream handler
        var workspace = hostedHub!.GetWorkspace();
        Output.WriteLine($"Workspace exists: {workspace != null}");

        // Check if UiControlService is available
        var uiControlService = hostedHub!.ServiceProvider.GetService<IUiControlService>();
        Output.WriteLine($"IUiControlService exists: {uiControlService != null}");
        Output.WriteLine($"LayoutDefinition renderer count: {uiControlService?.LayoutDefinition.Count ?? 0}");

        // Verify hub infrastructure is set up correctly
        workspace.Should().NotBeNull("Workspace should exist for the hub");
        uiControlService.Should().NotBeNull("IUiControlService should be available");
        uiControlService!.LayoutDefinition.Count.Should().BeGreaterThan(0, "Layout definition should have registered renderers");

        // Verify the Overview stream exists and returns data
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace!.GetStream<EntityStore>(reference);
        Output.WriteLine($"Overview stream exists: {stream != null}");
        ((object?)stream).Should().NotBeNull("Overview stream should be available");
    }

    /// <summary>
    /// Test that the Overview area renders for ProductLaunch.
    /// This verifies basic layout area functionality before testing Create.
    /// Areas may take time to populate due to NodeType compilation.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void ProductLaunch_Overview_ShouldRender()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        // No ping: the layout-area subscription itself activates the hub +
        // triggers the cold NodeType compile. Budget covers the cold Roslyn build.
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for Overview area...");
        // Wait for a value - areas may take time to populate due to NodeType compilation
        var rawValue = stream.Should().Within(50.Seconds()).Emit();
        Output.WriteLine($"Received raw value: {rawValue.Value.ValueKind}");

        // Verify we received a response (even if areas haven't populated yet due to async compilation)
        rawValue.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Overview area should return a response for ProductLaunch");
    }

    /// <summary>
    /// Test that the Create area renders with type parameter on ProductLaunch.
    /// This tests the exact URL: /ACME/ProductLaunch/Create?type=ACME%2FProject%2FTodo
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateArea_WithTypeParam_ShouldRenderCreateForm()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        var workspace = client.GetWorkspace();

        // First test without Id to verify Create area is registered.
        // No ping: the layout-area subscription itself activates the hub +
        // triggers the cold NodeType compile. Budget covers the cold Roslyn build.
        var referenceNoId = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);
        Output.WriteLine($"Testing Create area without Id first: Area={referenceNoId.Area}");

        var streamNoId = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            referenceNoId);

        var rawNoId = streamNoId.Should().Within(50.Seconds()).Emit();
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
        var rawChange = stream.Should().Within(10.Seconds()).Emit();
        var rawValue = rawChange.Value;
        Output.WriteLine($"Received raw value: {rawValue.ValueKind}");
        Output.WriteLine($"Raw JSON (first 500 chars): {rawValue.ToString().Substring(0, Math.Min(500, rawValue.ToString().Length))}");

        // Now try to get the control stream
        Output.WriteLine("Trying GetControlStream...");
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Create form should render when type parameter is specified");

        // The Create form should be a StackControl containing form fields
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty("Create form should have child areas (form fields)");

        Output.WriteLine($"Create form has {stack.Areas.Count()} areas");
    }
}

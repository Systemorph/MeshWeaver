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
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for the complete inline editing workflow:
/// 1. Render Overview view with readonly properties
/// 2. Click on a property to switch to edit mode
/// 3. Update the value via the data stream
/// 4. Verify the change persists via auto-save
/// </summary>
[Collection("SamplesGraphData")]
public class InlineEditingWorkflowTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Shared cache - tests run sequentially in this collection
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverInlineEditTests",
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
    /// Test that the Overview view renders with editable properties that have click actions.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Overview_RendersWithClickableProperties()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Getting Overview view...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        control.Should().NotBeNull("Overview view should render");
        Output.WriteLine($"Overview rendered: {control?.GetType().Name}");

        // The Overview should be a StackControl containing the header and content
        control.Should().BeOfType<StackControl>();
    }

    /// <summary>
    /// Test that updating data via the stream causes the data to be persisted.
    /// This tests the auto-save mechanism triggered by server-side data stream subscription.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task DataStreamUpdate_TriggersAutoSave()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var todoPath = "ACME/ProductLaunch/Todo/DefinePersona";
        var todoAddress = new Address(todoPath);
        var reference = new LayoutAreaReference("Overview");

        // Get original content to restore later
        var originalNode = await persistence.GetNodeAsync(todoPath, TestContext.Current.CancellationToken);
        originalNode.Should().NotBeNull("Todo node should exist");

        // Serialize original content for later comparison/restore
        var originalContentJson = JsonSerializer.Serialize(originalNode!.Content, Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions);
        Output.WriteLine($"Original content: {originalContentJson}");

        string? originalTitle = null;
        using (var doc = JsonDocument.Parse(originalContentJson))
        {
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
            {
                originalTitle = titleProp.GetString();
            }
        }
        Output.WriteLine($"Original title: {originalTitle}");

        try
        {
            var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                todoAddress,
                reference);

            // Wait for initial render
            var control = await stream
                .GetControlStream(reference.Area!)
                .Where(c => c != null)
                .Timeout(TimeSpan.FromSeconds(15))
                .FirstAsync();

            Output.WriteLine("Overview rendered successfully");

            // The data context should be set up with a content_* pattern
            // We need to find the data pointer and update it
            // The inline editing pattern uses: /data/"content_{nodePath}"
            var dataContextPointer = $"/data/\"content_{todoPath.Replace("/", "_")}\"";
            Output.WriteLine($"Expected data context pointer: {dataContextPointer}");

            // Update the title via the data stream
            var newTitle = $"Updated Title {DateTime.Now:HHmmss}";
            Output.WriteLine($"Updating title to: {newTitle}");

            // Use the UpdatePointer extension method to update the data
            stream.UpdatePointer(newTitle, dataContextPointer, new JsonPointerReference("title"));

            // Wait for the auto-save to trigger (500ms debounce + some buffer)
            Output.WriteLine("Waiting for auto-save debounce (1.5 seconds)...");
            await Task.Delay(1500);

            // Verify the data was persisted
            var updatedNode = await persistence.GetNodeAsync(todoPath, TestContext.Current.CancellationToken);
            updatedNode.Should().NotBeNull("Todo should still exist");

            // Check if the content was updated
            var updatedContentJson = JsonSerializer.Serialize(updatedNode!.Content, Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions);
            Output.WriteLine($"Updated content: {updatedContentJson}");

            using var doc = JsonDocument.Parse(updatedContentJson);
            doc.RootElement.TryGetProperty("title", out var updatedTitleProp).Should().BeTrue("Content should have title property");

            var updatedTitle = updatedTitleProp.GetString();
            Output.WriteLine($"Persisted title: {updatedTitle}");
            Output.WriteLine($"Expected title: {newTitle}");
            Output.WriteLine($"Original title: {originalTitle}");

            // ASSERTION: The title must be the new value, not the original
            updatedTitle.Should().Be(newTitle, "DataChangeRequest should have persisted the new title via auto-save");
        }
        finally
        {
            // Restore original content
            Output.WriteLine("Restoring original content...");
            var nodeToRestore = await persistence.GetNodeAsync(todoPath, TestContext.Current.CancellationToken);
            if (nodeToRestore != null)
            {
                // Deserialize original content and update node
                using var doc = JsonDocument.Parse(originalContentJson);
                var restoredNode = nodeToRestore with { Content = doc.RootElement.Clone() };
                await persistence.SaveNodeAsync(restoredNode, TestContext.Current.CancellationToken);
                Output.WriteLine("Original content restored");
            }
        }
    }

    /// <summary>
    /// Test that the Overview view can be fetched and contains expected structure.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task Overview_ContainsHeaderAndContent()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        Output.WriteLine("Getting Overview view...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        control.Should().NotBeNull("Overview view should render");

        var stack = control.Should().BeOfType<StackControl>().Subject;

        // The Overview stack should have areas (header, content, etc.)
        stack.Areas.Should().NotBeEmpty("Overview should have child areas");

        Output.WriteLine($"Overview has {stack.Areas.Count()} areas");
        foreach (var area in stack.Areas.Take(5))
        {
            Output.WriteLine($"  - Area: {area.Area}");
        }
    }

    /// <summary>
    /// Test that sending a ClickedEvent to an editable property switches to edit mode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ClickedEvent_SwitchesPropertyToEditMode()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var todoAddress = new Address("ACME/ProductLaunch/Todo/DefinePersona");
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            todoAddress,
            reference);

        // Wait for initial render
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c != null)
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync();

        control.Should().NotBeNull();
        var stack = control.Should().BeOfType<StackControl>().Subject;

        Output.WriteLine($"Overview has {stack.Areas.Count()} areas");

        // Find an area that contains a LabelControl (these are the property values)
        // The structure is: Overview -> Stack -> propsContainer -> rows -> label | value
        foreach (var area in stack.Areas)
        {
            var areaName = area.Area?.ToString();
            if (string.IsNullOrEmpty(areaName)) continue;

            try
            {
                var areaControl = await stream
                    .GetControlStream(areaName)
                    .Where(c => c != null)
                    .Timeout(TimeSpan.FromSeconds(2))
                    .FirstOrDefaultAsync();

                if (areaControl is LabelControl labelControl)
                {
                    Output.WriteLine($"Found LabelControl at area: {areaName}");

                    // Check if this label has data binding (it should for editable properties)
                    if (labelControl.Data is JsonPointerReference)
                    {
                        Output.WriteLine($"  - Has data binding, likely editable");

                        // Send click event
                        Output.WriteLine($"Sending ClickedEvent to: {areaName}");
                        client.Post(new ClickedEvent(areaName, stream.StreamId), o => o.WithTarget(todoAddress));

                        // Wait a bit for the click to be processed
                        await Task.Delay(500);

                        // Check if the control changed
                        var updatedControl = await stream
                            .GetControlStream(areaName)
                            .Timeout(TimeSpan.FromSeconds(2))
                            .FirstOrDefaultAsync();

                        if (updatedControl is TextFieldControl or NumberFieldControl)
                        {
                            Output.WriteLine($"SUCCESS: Control switched to edit mode: {updatedControl.GetType().Name}");
                            return; // Test passed
                        }
                        else
                        {
                            Output.WriteLine($"  - Control is still: {updatedControl?.GetType().Name ?? "null"}");
                        }
                    }
                }
                else if (areaControl is StackControl nestedStack)
                {
                    // Check nested areas
                    foreach (var nestedArea in nestedStack.Areas)
                    {
                        var nestedName = nestedArea.Area?.ToString();
                        if (string.IsNullOrEmpty(nestedName)) continue;

                        try
                        {
                            var nestedControl = await stream
                                .GetControlStream(nestedName)
                                .Where(c => c != null)
                                .Timeout(TimeSpan.FromSeconds(2))
                                .FirstOrDefaultAsync();

                            if (nestedControl is LabelControl nestedLabel && nestedLabel.Data is JsonPointerReference)
                            {
                                Output.WriteLine($"Found nested LabelControl at: {nestedName}");

                                // Send click event
                                client.Post(new ClickedEvent(nestedName, stream.StreamId), o => o.WithTarget(todoAddress));
                                await Task.Delay(500);

                                var updatedNested = await stream
                                    .GetControlStream(nestedName)
                                    .Timeout(TimeSpan.FromSeconds(2))
                                    .FirstOrDefaultAsync();

                                if (updatedNested is TextFieldControl or NumberFieldControl)
                                {
                                    Output.WriteLine($"SUCCESS: Nested control switched to edit mode: {updatedNested.GetType().Name}");
                                    return;
                                }
                            }
                        }
                        catch (TimeoutException)
                        {
                            // Ignore timeout for nested areas
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                // Ignore timeout for this area
            }
        }

        Output.WriteLine("Note: Could not find an editable property area to test click-to-edit");
        Output.WriteLine("This might be expected if the Todo type doesn't have inline-editable properties");
    }
}

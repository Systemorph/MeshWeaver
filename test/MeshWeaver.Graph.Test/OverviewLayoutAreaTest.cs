using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for OverviewLayoutArea - read-only display with click-to-edit functionality.
/// Uses fast in-memory test patterns - no file system, no delays.
/// </summary>
public class OverviewLayoutAreaTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string OverviewView = nameof(OverviewView);
    private const string TestDataId = "testEntity";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddData(data => data
                .AddSource(ds => ds
                    .WithType<TestTodo>(t => t
                        .WithInitialData(TestTodo.InitialData))))
            .AddLayout(layout => layout
                .WithView(OverviewView, BuildOverviewView)
                .WithView(nameof(MarkdownOverviewView), MarkdownOverviewView)
                .WithView(nameof(EditToggleView), EditToggleView));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// Builds an overview view using EditLayoutArea.Overview pattern.
    /// </summary>
    private static UiControl BuildOverviewView(LayoutAreaHost host, RenderingContext ctx)
    {
        var entity = TestTodo.InitialData[0];
        var dataId = TestDataId;
        host.UpdateData(dataId, entity);

        return EditLayoutArea.Overview(host, typeof(TestTodo), dataId, canEdit: true);
    }

    /// <summary>
    /// View with markdown property for testing SeparateEditView.
    /// </summary>
    private static UiControl MarkdownOverviewView(LayoutAreaHost host, RenderingContext ctx)
    {
        var entity = new TestDocument { Id = "doc1", Title = "Test Doc", Content = "# Hello\n\nWorld" };
        var dataId = "docEntity";
        host.UpdateData(dataId, entity);

        return EditLayoutArea.Overview(host, typeof(TestDocument), dataId, canEdit: true);
    }

    /// <summary>
    /// View for testing edit toggle functionality.
    /// </summary>
    private static UiControl EditToggleView(LayoutAreaHost host, RenderingContext ctx)
    {
        var entity = TestTodo.InitialData[0];
        var dataId = "editToggleData";
        host.UpdateData(dataId, entity);

        // Use MapToToggleableControl directly for a single property
        var prop = typeof(TestTodo).GetProperty(nameof(TestTodo.Category))!;
        return host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit: true, host);
    }

    [HubFact]
    public async Task ReadOnlyView_DisplaysStackControl()
    {
        var reference = new LayoutAreaReference(OverviewView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>();
    }

    [HubFact]
    public async Task ReadOnlyView_HasLayoutGrid()
    {
        var reference = new LayoutAreaReference(OverviewView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty();

        // First area should be the LayoutGrid with properties
        var firstAreaName = stack.Areas.First().Area?.ToString();
        firstAreaName.Should().NotBeNullOrEmpty();

        var gridControl = await stream
            .GetControlStream(firstAreaName!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        gridControl.Should().BeOfType<LayoutGridControl>();
    }

    [HubFact]
    public async Task ReadOnlyView_PropertiesHaveDataBinding()
    {
        var reference = new LayoutAreaReference(OverviewView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // Get data from stream
        var data = await stream
            .GetDataStream<TestTodo>(new JsonPointerReference($"/data/\"{TestDataId}\""))
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        data.Should().NotBeNull();
        data!.Category.Should().Be("Work");
        data.Priority.Should().Be(1);
    }

    [HubFact]
    public async Task ClickOnProperty_SwitchesToEditControl()
    {
        var reference = new LayoutAreaReference(nameof(EditToggleView));
        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // Get the initial control - should be a Stack with Label
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>();
        var stack = (StackControl)control!;

        // Find the clickable area (second area after label)
        var areas = stack.Areas.ToList();
        areas.Should().HaveCountGreaterThanOrEqualTo(2);

        var clickableArea = areas[1].Area?.ToString();
        clickableArea.Should().NotBeNullOrEmpty();

        // Send click event
        hub.Post(new ClickedEvent(clickableArea!, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for edit control to appear
        var editControl = await stream
            .GetControlStream(clickableArea!)
            .Where(c => c is TextFieldControl or NumberFieldControl or SelectControl)
            .Timeout(3.Seconds())
            .FirstAsync();

        editControl.Should().NotBeNull("should switch to an edit control");
    }

    [HubFact]
    public async Task MarkdownView_RendersStackControl()
    {
        var reference = new LayoutAreaReference(nameof(MarkdownOverviewView));
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>();
    }

    [HubFact]
    public async Task IsTitleProperty_FiltersCorrectly()
    {
        // Unit test for the static helper
        EditLayoutArea.IsTitleProperty("Title").Should().BeTrue();
        EditLayoutArea.IsTitleProperty("title").Should().BeTrue();
        EditLayoutArea.IsTitleProperty("Name").Should().BeTrue();
        EditLayoutArea.IsTitleProperty("DisplayName").Should().BeTrue();
        EditLayoutArea.IsTitleProperty("Category").Should().BeFalse();
        EditLayoutArea.IsTitleProperty("Description").Should().BeFalse();

        await Task.CompletedTask;
    }

    [HubFact]
    public async Task GetDataId_GeneratesConsistentId()
    {
        EditLayoutArea.GetDataId("Demos/ACME/Project/Todo").Should().Be("content_ACME_Project_Todo");
        EditLayoutArea.GetDataId("path/with/slashes").Should().Be("content_path_with_slashes");

        await Task.CompletedTask;
    }
}

/// <summary>
/// Test entity with regular properties (no markdown).
/// </summary>
public record TestTodo(
    [property: Key] string Id,
    string Category,
    int Priority,
    bool IsComplete,
    DateTime? DueDate)
{
    [Browsable(false)]
    public string Title => $"Todo {Id}";

    public static readonly TestTodo[] InitialData =
    [
        new("1", "Work", 1, false, DateTime.Today.AddDays(7)),
        new("2", "Personal", 2, true, null)
    ];
}

/// <summary>
/// Test entity with markdown property.
/// </summary>
public record TestDocument
{
    [Key]
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    [UiControl<MarkdownEditorControl, MarkdownControl>(SeparateEditView = true)]
    public string Content { get; init; } = "";
}

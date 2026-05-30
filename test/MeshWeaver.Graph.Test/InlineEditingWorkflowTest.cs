using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
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
/// Tests for the inline editing workflow using fast in-memory patterns.
/// </summary>
public class InlineEditingWorkflowTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string OverviewView = nameof(OverviewView);
    private const string EditableView = nameof(EditableView);
    private const string MarkdownView = nameof(MarkdownView);
    private const string TestDataId = "editableEntity";

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddData(data => data
                .AddSource(ds => ds
                    .WithType<EditableItem>(t => t
                        .WithInitialData(EditableItem.InitialData))))
            .AddLayout(layout => layout
                .WithView(OverviewView, BuildOverviewView)
                .WithView(EditableView, BuildEditableView)
                .WithView(MarkdownView, BuildMarkdownView));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private static UiControl BuildOverviewView(LayoutAreaHost host, RenderingContext ctx)
    {
        var entity = EditableItem.InitialData[0];
        host.UpdateData(TestDataId, entity);
        return EditLayoutArea.Overview(host, typeof(EditableItem), TestDataId, canEdit: true);
    }

    private static UiControl BuildEditableView(LayoutAreaHost host, RenderingContext ctx)
    {
        var entity = EditableItem.InitialData[0];
        var dataId = "singleProp";
        host.UpdateData(dataId, entity);

        // Single toggleable property for click-to-edit test
        var prop = typeof(EditableItem).GetProperty(nameof(EditableItem.Category))!;
        return host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit: true, host);
    }

    private static UiControl BuildMarkdownView(LayoutAreaHost host, RenderingContext ctx)
    {
        var entity = new MarkdownItem { Id = "md1", Title = "Doc", Content = "# Hello" };
        var dataId = "markdownEntity";
        host.UpdateData(dataId, entity);
        return EditLayoutArea.Overview(host, typeof(MarkdownItem), dataId, canEdit: true);
    }

    [HubFact]
    public void Overview_RendersWithClickableProperties()
    {
        var reference = new LayoutAreaReference(OverviewView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(5.Seconds()).Match(x => x != null);

        control.Should().BeOfType<StackControl>();
        var stack = (StackControl)control!;
        stack.Areas.Should().NotBeEmpty("Overview should have child areas");
    }

    [HubFact]
    public void Overview_ContainsLayoutGrid()
    {
        var reference = new LayoutAreaReference(OverviewView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(5.Seconds()).Match(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        var firstArea = stack.Areas.First().Area?.ToString();
        firstArea.Should().NotBeNullOrEmpty();

        var gridControl = stream
            .GetControlStream(firstArea!)
            .Should().Within(5.Seconds()).Match(x => x != null);

        gridControl.Should().BeOfType<LayoutGridControl>();
    }

    [HubFact]
    public void ClickedEvent_SwitchesToEditMode()
    {
        var reference = new LayoutAreaReference(EditableView);
        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // Get initial control - Stack with Label and clickable area
        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(5.Seconds()).Match(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        var areas = stack.Areas.ToList();
        areas.Should().HaveCountGreaterThanOrEqualTo(2);

        // Second area is the clickable readonly value
        var clickableArea = areas[1].Area?.ToString();
        clickableArea.Should().NotBeNullOrEmpty();

        // Send click event
        hub.Post(new ClickedEvent(clickableArea!, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for edit control
        var editControl = stream
            .GetControlStream(clickableArea!)
            .Should().Within(3.Seconds()).Match(c => c is TextFieldControl or NumberFieldControl or SelectControl);

        editControl.Should().NotBeNull("should switch to edit control after click");
    }

    [HubFact]
    public void DataBinding_ReflectsInitialValues()
    {
        var reference = new LayoutAreaReference(OverviewView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // Verify data is bound correctly
        var data = stream
            .GetDataStream<EditableItem>(new JsonPointerReference($"/data/\"{TestDataId}\""))
            .Should().Within(5.Seconds()).Match(x => x != null);

        data.Should().NotBeNull();
        data!.Category.Should().Be("Work");
        data.Priority.Should().Be(1);
        data.IsComplete.Should().BeFalse();
    }

    [HubFact]
    public void DataUpdate_PropagatesViaStream()
    {
        var reference = new LayoutAreaReference(EditableView);
        var hub = GetClient();
        var workspace = hub.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        // Wait for initial render
        stream
            .GetControlStream(reference.Area!)
            .Should().Within(5.Seconds()).Match(x => x != null);

        // Update data via stream
        var dataPointer = "/data/\"singleProp\"";
        var newValue = new EditableItem("1", "Updated Category", 5, true, DateTime.Today);

        stream.UpdatePointer(newValue, dataPointer, new JsonPointerReference(""));

        // Verify the update propagated
        var updated = stream
            .GetDataStream<EditableItem>(new JsonPointerReference(dataPointer))
            .Should().Within(3.Seconds()).Match(x => x?.Category == "Updated Category");

        updated.Should().NotBeNull();
        updated!.Category.Should().Be("Updated Category");
        updated.Priority.Should().Be(5);
    }

    [HubFact]
    public void MarkdownView_RendersCorrectly()
    {
        var reference = new LayoutAreaReference(MarkdownView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = stream
            .GetControlStream(reference.Area!)
            .Should().Within(5.Seconds()).Match(x => x != null);

        control.Should().BeOfType<StackControl>();
    }

}

/// <summary>
/// Test entity for inline editing tests.
/// </summary>
public record EditableItem(
    [property: Key] string Id,
    string Category,
    int Priority,
    bool IsComplete,
    DateTime? DueDate)
{
    [Browsable(false)]
    public string Title => $"Item {Id}";

    public static readonly EditableItem[] InitialData =
    [
        new("1", "Work", 1, false, DateTime.Today.AddDays(7)),
        new("2", "Personal", 2, true, null)
    ];
}

/// <summary>
/// Test entity with markdown content.
/// </summary>
public record MarkdownItem
{
    [Key]
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    [UiControl<MarkdownEditorControl, MarkdownControl>(SeparateEditView = true)]
    public string Content { get; init; } = "";
}

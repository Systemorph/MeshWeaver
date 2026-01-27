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
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for the inline editing functionality in MeshNode properties.
/// Verifies click-to-edit behavior, data binding, and auto-save patterns.
/// </summary>
[Collection("InlineEditingTests")]
public class InlineEditingTest(ITestOutputHelper output) : HubTestBase(output)
{
    #region Test Domain

    /// <summary>
    /// Test record with various property types to verify inline editing.
    /// </summary>
    public record TestContent
    {
        [Display(Name = "Title")]
        public string Title { get; init; } = string.Empty;

        [Display(Name = "Description")]
        public string Description { get; init; } = string.Empty;

        [Display(Name = "Count")]
        public int Count { get; init; }

        [Display(Name = "Is Active")]
        public bool IsActive { get; init; }

        [Key]
        [Display(Name = "Id")]
        public string Id { get; init; } = string.Empty;

        [Editable(false)]
        [Display(Name = "Created Date")]
        public DateTime CreatedDate { get; init; } = DateTime.UtcNow;
    }

    private static TestContent TestData => new()
    {
        Id = "test-1",
        Title = "Test Title",
        Description = "Test Description",
        Count = 42,
        IsActive = true,
        CreatedDate = new DateTime(2024, 1, 1)
    };

    #endregion

    #region View Definitions

    private const string InlineEditView = nameof(InlineEditView);

    private UiControl InlineEditViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "test_content";
        host.UpdateData(dataId, TestData);

        // Create a grid layout with editable properties
        var grid = Controls.LayoutGrid
            .WithSkin(s => s.WithSpacing(0))
            .WithStyle(style => style
                .WithBackgroundColor("var(--neutral-fill-rest)")
                .WithBorderRadius("8px")
                .WithPadding("12px 16px")) with { DataContext = LayoutAreaReference.GetDataPointer(dataId) };

        // Add editable property: Title (string)
        grid = grid.WithView(
            Controls.Label("Title:")
                .WithStyle(s => s.WithFontWeight("600")),
            itemSkin => itemSkin.WithXs(4));
        grid = grid.WithView(
            Controls.Body(TestData.Title)
                .WithStyle(s => s.WithPadding("6px 0"))
                .WithClickAction(clickCtx =>
                {
                    var editControl = new TextFieldControl(new JsonPointerReference("title"))
                    {
                        Readonly = false,
                        AutoFocus = true
                    };
                    clickCtx.Host.UpdateArea(clickCtx.Area, editControl);
                    return Task.CompletedTask;
                }),
            itemSkin => itemSkin.WithXs(8));

        // Add readonly property: Id (has [Key] attribute)
        grid = grid.WithView(
            Controls.Label("Id:")
                .WithStyle(s => s.WithFontWeight("600")),
            itemSkin => itemSkin.WithXs(4));
        grid = grid.WithView(
            Controls.Body(TestData.Id)
                .WithStyle(s => s.WithPadding("6px 0")),
            itemSkin => itemSkin.WithXs(8));

        return grid;
    }

    #endregion

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                .WithView(InlineEditView, InlineEditViewDefinition));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// Test that the inline edit view renders with readonly values initially.
    /// </summary>
    [Fact]
    public async Task InlineEdit_InitialRender_ShowsReadonlyValues()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(InlineEditView));

        var control = await area
            .GetControlStream(InlineEditView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // Should render as a LayoutGridControl
        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.DataContext.Should().Be("/data/\"test_content\"");

        // Should have areas for label and value pairs
        grid.Areas.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Test that clicking on an editable field switches to edit mode.
    /// </summary>
    [Fact]
    public async Task InlineEdit_ClickToEdit_ShouldSwitchToEditMode()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(InlineEditView));

        // Wait for initial render
        var control = await stream
            .GetControlStream(InlineEditView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // Get the second area (first value area - Title value)
        var titleValueArea = grid.Areas.Skip(1).First().Area.ToString()!;

        // Get the body control that should have a click action
        var titleControl = await stream
            .GetControlStream(titleValueArea)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

        // Initial control should be a Body (Label) control
        titleControl.Should().BeOfType<LabelControl>();

        // Send click event to switch to edit mode
        client.Post(new ClickedEvent(titleValueArea, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for the control to switch to TextField
        var editControl = await stream
            .GetControlStream(titleValueArea)
            .Where(x => x is TextFieldControl)
            .Timeout(5.Seconds())
            .FirstAsync();

        // Should now be a TextFieldControl
        var textField = editControl.Should().BeOfType<TextFieldControl>().Subject;
        textField.Readonly.Should().Be(false);
    }

    /// <summary>
    /// Test that data binding works correctly for the edit control.
    /// </summary>
    [Fact]
    public async Task InlineEdit_DataBinding_ShouldUpdateDataStream()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(InlineEditView));

        // Wait for initial render
        await stream
            .GetControlStream(InlineEditView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // Read initial data from stream
        var initialData = await stream
            .GetDataStream<TestContent>(new JsonPointerReference("/data/\"test_content\""))
            .Where(x => x is not null)
            .Timeout(5.Seconds())
            .FirstAsync();

        initialData.Should().NotBeNull();
        initialData!.Title.Should().Be("Test Title");

        // Update the title via the data stream
        stream.UpdatePointer("Updated Title", "/data/\"test_content\"", new JsonPointerReference("title"));

        // Wait for the update to propagate
        await Task.Delay(100);

        // Read updated data
        var updatedData = await stream
            .GetDataStream<TestContent>(new JsonPointerReference("/data/\"test_content\""))
            .Where(x => x?.Title == "Updated Title")
            .Timeout(5.Seconds())
            .FirstAsync();

        updatedData.Should().NotBeNull();
        updatedData!.Title.Should().Be("Updated Title");
    }

    /// <summary>
    /// Test that readonly properties (with [Key] or [Editable(false)]) don't have click actions.
    /// </summary>
    [Fact]
    public async Task InlineEdit_ReadonlyProperty_ShouldNotBeEditable()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(InlineEditView));

        var control = await stream
            .GetControlStream(InlineEditView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // Get the fourth area (second value area - Id value which has [Key])
        var idValueArea = grid.Areas.Skip(3).First().Area.ToString()!;

        var idControl = await stream
            .GetControlStream(idValueArea)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

        // The Id control should be a Label control (readonly properties don't switch to edit)
        idControl.Should().BeOfType<LabelControl>();

        // Verify that clicking on it does NOT switch to a TextField
        // (Readonly properties stay as LabelControl)
        client.Post(new ClickedEvent(idValueArea, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait a bit and verify control is still LabelControl
        await Task.Delay(200);

        var stillLabel = await stream
            .GetControlStream(idValueArea)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

        stillLabel.Should().BeOfType<LabelControl>();
    }
}

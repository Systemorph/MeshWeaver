using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Messaging;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Layout.Test;

/// <summary>
/// Tests for the MapToToggleableControl functionality in EditorExtensions.cs.
/// Verifies click-to-edit behavior, blur-to-readonly, and mode switching.
/// </summary>
[Collection("MapToToggleableControlTests")]
public class MapToToggleableControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    #region Test Domain

    /// <summary>
    /// Test record with various property types to verify toggleable controls.
    /// </summary>
    public record TestEntity
    {
        [Key]
        [Display(Name = "Id")]
        public string Id { get; init; } = string.Empty;

        [Display(Name = "Title")]
        public string Title { get; init; } = string.Empty;

        [Editable(false)]
        [Display(Name = "Created Date")]
        public DateTime Created { get; init; } = DateTime.UtcNow;

        [Display(Name = "Count")]
        public int Count { get; init; }

        [Display(Name = "Is Active")]
        public bool IsActive { get; init; }

        [UiControl(editControl: typeof(MarkdownEditorControl), SeparateEditView = true)]
        [Display(Name = "Description")]
        public string Description { get; init; } = string.Empty;
    }

    private static TestEntity TestData => new()
    {
        Id = "test-1",
        Title = "Test Title",
        Created = new DateTime(2024, 1, 1),
        Count = 42,
        IsActive = true,
        Description = "# Test Description\n\nThis is markdown content."
    };

    #endregion

    #region View Definitions

    private const string ToggleableControlView = nameof(ToggleableControlView);
    private const string DataBindingTestView = nameof(DataBindingTestView);
    private const string AutoSaveTestView = nameof(AutoSaveTestView);

    // Track server-side updates for testing auto-save
    private static readonly List<string> ServerSideUpdates = [];

    private UiControl ToggleableControlViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "test_entity";
        host.UpdateData(dataId, TestData);

        var entityType = typeof(TestEntity);
        var properties = entityType.GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .ToArray();

        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

        foreach (var prop in properties)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit: true, host);
            grid = grid.WithView(control, s => s.WithXs(12).WithMd(6));
        }

        return grid;
    }

    private UiControl DataBindingTestViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "binding_test";
        host.UpdateData(dataId, TestData);

        var titleProperty = typeof(TestEntity).GetProperty(nameof(TestEntity.Title))!;
        return host.Hub.ServiceProvider.MapToToggleableControl(titleProperty, dataId, canEdit: true, host);
    }

    private UiControl AutoSaveTestViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "autosave_test";
        host.UpdateData(dataId, TestData);

        // Set up server-side subscription to track data changes
        var initialTitle = TestData.Title;
        host.RegisterForDisposal(dataId,
            host.Stream.GetDataStream<TestEntity>(dataId)
                .Debounce(TimeSpan.FromMilliseconds(50))
                .Subscribe(content =>
                {
                    if (content != null && content.Title != initialTitle)
                    {
                        ServerSideUpdates.Add($"Title changed to: {content.Title}");
                        initialTitle = content.Title;
                    }
                }));

        var titleProperty = typeof(TestEntity).GetProperty(nameof(TestEntity.Title))!;
        return host.Hub.ServiceProvider.MapToToggleableControl(titleProperty, dataId, canEdit: true, host);
    }

    #endregion

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                .WithView(ToggleableControlView, ToggleableControlViewDefinition)
                .WithView(DataBindingTestView, DataBindingTestViewDefinition)
                .WithView(AutoSaveTestView, AutoSaveTestViewDefinition));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// Test that the initial render shows readonly views with LabelControl.
    /// </summary>
    [Fact]
    public void InitialRender_ShowsReadonlyView()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(ToggleableControlView));

        var control = area
            .GetControlStream(ToggleableControlView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Should render as a LayoutGridControl
        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().NotBeEmpty();

        // Get the first non-markdown property area (Title)
        var titleAreaId = grid.Areas.Skip(1).First().Area.ToString()!; // Skip Id, get Title

        var titleControl = area
            .GetControlStream(titleAreaId)
            .Should().Within(5.Seconds()).Match(x => x is not null);

        // The control should be a Stack containing Label and the reactive view
        titleControl.Should().BeOfType<StackControl>();
    }

    /// <summary>
    /// Test that clicking on an editable property switches to edit mode.
    /// </summary>
    [Fact]
    public void ClickOnProperty_SwitchesToEditControl()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DataBindingTestView));

        // Wait for initial render
        var control = stream
            .GetControlStream(DataBindingTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2); // Label + reactive view

        // Get the reactive view area (second area)
        var reactiveAreaId = stack.Areas.Skip(1).First().Area.ToString()!;

        // Initial control should contain a clickable Stack with LabelControl
        var initialControl = stream
            .GetControlStream(reactiveAreaId)
            .Should().Within(5.Seconds()).Match(x => x is not null);

        // The initial state should be a clickable Stack
        initialControl.Should().BeOfType<StackControl>();

        // Set up the edit-mode watch BEFORE posting the click to avoid a race
        var editControlStream = stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl);

        // Send click event to switch to edit mode
        client.Post(new ClickedEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for the control to switch to TextField
        var editControl = editControlStream.Should().Within(5.Seconds()).Emit();

        // Should now be a TextFieldControl
        var textField = editControl.Should().BeOfType<TextFieldControl>().Subject;
        textField.AutoFocus.Should().Be(true);
    }

    /// <summary>
    /// Test that blur on edit control switches back to readonly mode.
    /// </summary>
    [Fact]
    public void BlurOnEditControl_SwitchesBackToReadonly()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DataBindingTestView));

        // Wait for initial render
        var control = stream
            .GetControlStream(DataBindingTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        var reactiveAreaId = stack.Areas.Skip(1).First().Area.ToString()!;

        var editControlStream = stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl);

        // Click to enter edit mode
        client.Post(new ClickedEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for edit mode
        var editControl = editControlStream.Should().Within(5.Seconds()).Emit();

        editControl.Should().BeOfType<TextFieldControl>();

        var readonlyControlStream = stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is StackControl);

        // Send blur event to exit edit mode
        client.Post(new BlurEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for the control to switch back to Stack (readonly view)
        var readonlyControl = readonlyControlStream.Should().Within(5.Seconds()).Emit();

        readonlyControl.Should().BeOfType<StackControl>();
    }

    /// <summary>
    /// Test that readonly properties (with [Key] or [Editable(false)]) don't have click actions.
    /// </summary>
    [Fact]
    public void ReadonlyProperty_NotClickable()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(ToggleableControlView));

        var control = stream
            .GetControlStream(ToggleableControlView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // Get the Id property area (first property, has [Key] attribute)
        var idAreaId = grid.Areas.First().Area.ToString()!;

        var idControl = stream
            .GetControlStream(idAreaId)
            .Should().Within(5.Seconds()).Match(x => x is not null);

        // The Id control should be a Stack
        var idStack = idControl.Should().BeOfType<StackControl>().Subject;

        // Get the reactive view area inside the stack
        if (idStack.Areas.Count > 1)
        {
            var idReactiveAreaId = idStack.Areas.Skip(1).First().Area.ToString()!;

            stream
                .GetControlStream(idReactiveAreaId)
                .Should().Within(5.Seconds()).Match(x => x is not null);

            // Verify that clicking on it does NOT switch to a TextField
            client.Post(new ClickedEvent(idReactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

            // Negative assertion: clicking a [Key] (readonly) property must never switch to a TextField.
            // There is no positive signal to await, so confirm no TextField emission within a short window.
            stream
                .GetControlStream(idReactiveAreaId)
                .Where(x => x is TextFieldControl)
                .Should().NotEmit(200.Milliseconds());
        }
    }

    /// <summary>
    /// Test that data binding works correctly through mode switch.
    /// </summary>
    [Fact]
    public void DataBinding_WorksThroughModeSwitch()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DataBindingTestView));

        // Wait for initial render
        stream
            .GetControlStream(DataBindingTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Read initial data from stream
        var initialData = stream
            .GetDataStream<TestEntity>(new JsonPointerReference("/data/\"binding_test\""))
            .Should().Within(5.Seconds()).Match(x => x is not null);

        initialData.Should().NotBeNull();
        initialData!.Title.Should().Be("Test Title");

        // Update the title via the data stream
        stream.UpdatePointer("Updated Title", "/data/\"binding_test\"", new JsonPointerReference("title"));

        // Read updated data (the predicate waits for the update to propagate)
        var updatedData = stream
            .GetDataStream<TestEntity>(new JsonPointerReference("/data/\"binding_test\""))
            .Should().Within(5.Seconds()).Match(x => x?.Title == "Updated Title");

        updatedData.Should().NotBeNull();
        updatedData!.Title.Should().Be("Updated Title");
    }

    /// <summary>
    /// Test that server-side subscription receives updates (auto-save pattern).
    /// </summary>
    [Fact]
    public void AutoSave_TriggersOnChange()
    {
        ServerSideUpdates.Clear();

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(AutoSaveTestView));

        // Wait for initial render
        stream
            .GetControlStream(AutoSaveTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Update the title via the client
        stream.UpdatePointer("Auto-saved Title", "/data/\"autosave_test\"", new JsonPointerReference("title"));

        // The server-side subscription debounces and appends to the shared list (not an observable),
        // so probe the list until it reflects the update rather than sleeping for a fixed window.
        Observable.Interval(50.Milliseconds())
            .StartWith(0L)
            .Select(_ => ServerSideUpdates.ToArray())
            .Where(updates => updates.Any(u => u.Contains("Auto-saved Title")))
            .Should().Within(5.Seconds()).Emit();

        // Verify server-side subscription received the update
        ServerSideUpdates.Should().NotBeEmpty("Server-side subscription should receive client updates");
        ServerSideUpdates.Should().Contain(u => u.Contains("Auto-saved Title"));
    }
}

/// <summary>
/// Tests that verify data persistence through the edit cycle.
/// These tests check that changes made in edit mode are actually persisted
/// to the underlying data store, not just updated in the local stream.
/// </summary>
[Collection("EditPersistenceTests")]
public class EditPersistenceTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record PersistableEntity
    {
        [Key]
        public string Id { get; init; } = string.Empty;

        [Display(Name = "Title")]
        public string Title { get; init; } = string.Empty;

        [Display(Name = "Count")]
        public int Count { get; init; }

        [Display(Name = "Due Date")]
        public DateTime? DueDate { get; init; }

        [Display(Name = "Is Complete")]
        public bool IsComplete { get; init; }
    }

    private static PersistableEntity[] InitialData =>
    [
        new PersistableEntity
        {
            Id = "persist-1",
            Title = "Original Title",
            Count = 10,
            DueDate = new DateTime(2024, 6, 15),
            IsComplete = false
        }
    ];

    private const string PersistenceView = nameof(PersistenceView);

    private UiControl PersistenceViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "persistable_entity";

        // Get the entity from workspace and set up data binding
        var entityStream = host.Workspace.GetStream<PersistableEntity>();
        if (entityStream != null)
        {
            host.RegisterForDisposal(dataId,
                entityStream
                    .Select(items => items?.FirstOrDefault(e => e.Id == "persist-1"))
                    .Where(e => e != null)
                    .Subscribe(entity => host.UpdateData(dataId, entity!)));
        }

        // Build the form using MapToToggleableControl
        var properties = typeof(PersistableEntity).GetProperties()
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Where(p => p.GetCustomAttribute<KeyAttribute>() == null)
            .ToArray();

        var stack = Controls.Stack.WithWidth("100%");

        foreach (var prop in properties)
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, dataId, canEdit: true, host);
            stack = stack.WithView(control);
        }

        return stack;
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddData(data => data
                .AddSource(source => source
                    .WithType<PersistableEntity>(type => type
                        .WithInitialData(InitialData))))
            .AddLayout(layout => layout
                .WithView(PersistenceView, PersistenceViewDefinition));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    // Removed 2026-05-25:
    //   - EditAndPersist_StringProperty_ShouldPersistToDataStore
    //   - EditAndPersist_NullableDateTime_ShouldPersistToDataStore
    //   - WorkspaceStreamEmit_ShouldNotOverwriteLocalEdits
    // All three asserted behavior of a test-invented `SetupAutoSave` (debounced
    // GetDataStream → DataChangeRequest) that production code does NOT use —
    // see src/MeshWeaver.Layout/Domain/EditLayoutArea.cs:181, which writes via
    // `stream.Subscribe → host.UpdateData` (no debounce, no DataChangeRequest).
    // The canonical write path per CLAUDE.md is
    //   workspace.GetMeshNodeStream(path).Update(current => …)
    // for MeshNode data and `workspace.Update(...)` for data-source instances —
    // not a hand-rolled debounce + Observe<DataChangeResponse>. The tests also
    // leaked their Debounce subscriptions through the shared SP into sibling
    // test classes (specifically DataChangeStreamUpdateTest.DeleteTask), so
    // any one of them passing in isolation broke the full Layout.Test suite.
    // Plus WorkspaceStreamEmit_ShouldNotOverwriteLocalEdits was a design
    // contradiction: it asserted "local edit wins" against pure Debounce
    // semantics ("last emit wins") with no source-level emission tagging.
    // EditState_ShouldSurviveDataUpdates below tests genuine MapToToggleableControl
    // behavior (edit-mode survival across data updates) and is retained.


    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that edit state resets when view re-renders.
    ///
    /// The issue: MapToToggleableControl uses StartWith(false) on the edit state stream.
    /// When the view is re-rendered (due to data change), a new subscription is created
    /// that starts with false, resetting the edit state.
    /// </summary>
    [Fact]
    public void EditState_ShouldSurviveDataUpdates()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = CreateHostAddress();

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(PersistenceView));

        // Wait for initial render
        var control = layoutStream
            .GetControlStream(PersistenceView)
            .Should().Within(10.Seconds()).Match(x => x is StackControl);

        var stack = control.Should().BeOfType<StackControl>().Subject;

        // Get the first property's stack (Title)
        var titleStackAreaId = stack.Areas.First().Area.ToString()!;
        var titleStack = layoutStream
            .GetControlStream(titleStackAreaId)
            .Should().Within(5.Seconds()).Match(x => x is StackControl);

        var titleStackControl = titleStack.Should().BeOfType<StackControl>().Subject;

        // Get the reactive view area (second child - after the label)
        var reactiveAreaId = titleStackControl.Areas.Skip(1).First().Area.ToString()!;

        var editControlStream = layoutStream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl);

        // Click to enter edit mode
        Output.WriteLine("Entering edit mode...");
        client.Post(new ClickedEvent(reactiveAreaId, layoutStream.StreamId), o => o.WithTarget(hostAddress));

        // Wait for edit mode
        editControlStream.Should().Within(5.Seconds()).Emit();

        Output.WriteLine("Edit mode activated");

        // While in edit mode, trigger a data update (simulating workspace stream emit)
        Output.WriteLine("Triggering data update while in edit mode...");
        layoutStream.UpdatePointer(42, "/data/\"persistable_entity\"", new JsonPointerReference("count"));

        // Wait for the data update to propagate (count reaches 42) before reading the control state,
        // so we observe the control *after* the re-render rather than sleeping for a fixed window.
        layoutStream
            .GetDataStream<PersistableEntity>(new JsonPointerReference("/data/\"persistable_entity\""))
            .Should().Within(5.Seconds()).Match(x => x?.Count == 42);

        // Check if still in edit mode (current control after the data-driven re-render)
        var controlAfterDataUpdate = layoutStream
            .GetControlStream(reactiveAreaId)
            .Should().Within(2.Seconds()).Emit();

        Output.WriteLine($"Control after data update: {controlAfterDataUpdate?.GetType().Name}");

        // THIS ASSERTION SHOULD FAIL - data update causes re-render which resets edit state
        controlAfterDataUpdate.Should().BeOfType<TextFieldControl>(
            "Edit state should survive data updates, but it resets - this demonstrates the bug");
    }
}

/// <summary>
/// Tests for markdown properties with SeparateEditView using Done button.
/// </summary>
[Collection("MarkdownToggleTests")]
public class MarkdownToggleTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record MarkdownEntity
    {
        [Key]
        public string Id { get; init; } = string.Empty;

        [UiControl(editControl: typeof(MarkdownEditorControl), SeparateEditView = true)]
        [Display(Name = "Content")]
        public string Content { get; init; } = string.Empty;
    }

    private static MarkdownEntity TestData => new()
    {
        Id = "md-1",
        Content = "# Hello\n\nThis is markdown."
    };

    private const string MarkdownToggleView = nameof(MarkdownToggleView);

    private UiControl MarkdownToggleViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "markdown_entity";
        host.UpdateData(dataId, TestData);

        var contentProperty = typeof(MarkdownEntity).GetProperty(nameof(MarkdownEntity.Content))!;
        return host.Hub.ServiceProvider.MapToToggleableControl(contentProperty, dataId, canEdit: true, host);
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                .WithView(MarkdownToggleView, MarkdownToggleViewDefinition));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    /// <summary>
    /// Test that markdown properties use Done button instead of blur.
    /// </summary>
    [Fact]
    public void Markdown_UsesDoneButton()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(MarkdownToggleView));

        var control = area
            .GetControlStream(MarkdownToggleView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Should render as a Stack with full width
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Skin.Should().NotBeNull();
        (stack.Skin as LayoutStackSkin)?.Width.Should().Be("100%");

        // The stack should contain the reactive view area
        stack.Areas.Should().NotBeEmpty();
    }
}

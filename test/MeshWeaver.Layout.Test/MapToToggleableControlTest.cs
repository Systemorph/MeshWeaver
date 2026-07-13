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
    public async Task InitialRender_ShowsReadonlyView()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(ToggleableControlView));

        var control = await area
            .GetControlStream(ToggleableControlView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Should render as a LayoutGridControl
        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().NotBeEmpty();

        // Get the first non-markdown property area (Title)
        var titleAreaId = grid.Areas.Skip(1).First().Area.ToString()!; // Skip Id, get Title

        var titleControl = await area
            .GetControlStream(titleAreaId)
            .Should().Within(5.Seconds()).Match(x => x is not null);

        // The control should be a Stack containing Label and the reactive view
        titleControl.Should().BeOfType<StackControl>();
    }

    /// <summary>
    /// Test that clicking on an editable property switches to edit mode.
    /// </summary>
    [Fact]
    public async Task ClickOnProperty_SwitchesToEditControl()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DataBindingTestView));

        // Wait for initial render
        var control = await stream
            .GetControlStream(DataBindingTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2); // Label + reactive view

        // Get the reactive view area (second area)
        var reactiveAreaId = stack.Areas.Skip(1).First().Area.ToString()!;

        // Initial control should contain a clickable Stack with LabelControl
        var initialControl = await stream
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
        var editControl = await editControlStream.Should().Within(5.Seconds()).Emit();

        // Should now be a TextFieldControl
        var textField = editControl.Should().BeOfType<TextFieldControl>().Subject;
        textField.AutoFocus.Should().Be(true);
    }

    /// <summary>
    /// Test that blur on edit control switches back to readonly mode.
    /// </summary>
    [Fact]
    public async Task BlurOnEditControl_SwitchesBackToReadonly()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DataBindingTestView));

        // Wait for initial render
        var control = await stream
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
        var editControl = await editControlStream.Should().Within(5.Seconds()).Emit();

        editControl.Should().BeOfType<TextFieldControl>();

        var readonlyControlStream = stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is StackControl);

        // Send blur event to exit edit mode
        client.Post(new BlurEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for the control to switch back to Stack (readonly view)
        var readonlyControl = await readonlyControlStream.Should().Within(5.Seconds()).Emit();

        readonlyControl.Should().BeOfType<StackControl>();
    }

    /// <summary>
    /// Test that readonly properties (with [Key] or [Editable(false)]) don't have click actions.
    /// </summary>
    [Fact]
    public async Task ReadonlyProperty_NotClickable()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(ToggleableControlView));

        var control = await stream
            .GetControlStream(ToggleableControlView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // Get the Id property area (first property, has [Key] attribute)
        var idAreaId = grid.Areas.First().Area.ToString()!;

        var idControl = await stream
            .GetControlStream(idAreaId)
            .Should().Within(5.Seconds()).Match(x => x is not null);

        // The Id control should be a Stack
        var idStack = idControl.Should().BeOfType<StackControl>().Subject;

        // Get the reactive view area inside the stack
        if (idStack.Areas.Count > 1)
        {
            var idReactiveAreaId = idStack.Areas.Skip(1).First().Area.ToString()!;

            await stream
                .GetControlStream(idReactiveAreaId)
                .Should().Within(5.Seconds()).Match(x => x is not null);

            // Verify that clicking on it does NOT switch to a TextField
            client.Post(new ClickedEvent(idReactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

            // Negative assertion: clicking a [Key] (readonly) property must never switch to a TextField.
            // There is no positive signal to await, so confirm no TextField emission within a short window.
            await stream
                .GetControlStream(idReactiveAreaId)
                .Where(x => x is TextFieldControl)
                .Should().NotEmit(200.Milliseconds());
        }
    }

    /// <summary>
    /// Test that data binding works correctly through mode switch.
    /// </summary>
    [Fact]
    public async Task DataBinding_WorksThroughModeSwitch()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(DataBindingTestView));

        // Wait for initial render
        await stream
            .GetControlStream(DataBindingTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Read initial data from stream
        var initialData = await stream
            .GetDataStream<TestEntity>(new JsonPointerReference("/data/\"binding_test\""))
            .Should().Within(5.Seconds()).Match(x => x is not null);

        initialData.Should().NotBeNull();
        initialData!.Title.Should().Be("Test Title");

        // Update the title via the data stream
        stream.UpdatePointer("Updated Title", "/data/\"binding_test\"", new JsonPointerReference("title"));

        // Read updated data (the predicate waits for the update to propagate)
        var updatedData = await stream
            .GetDataStream<TestEntity>(new JsonPointerReference("/data/\"binding_test\""))
            .Should().Within(5.Seconds()).Match(x => x?.Title == "Updated Title");

        updatedData.Should().NotBeNull();
        updatedData!.Title.Should().Be("Updated Title");
    }

    /// <summary>
    /// Test that server-side subscription receives updates (auto-save pattern).
    /// </summary>
    [Fact]
    public async Task AutoSave_TriggersOnChange()
    {
        ServerSideUpdates.Clear();

        var client = GetClient();
        var workspace = client.GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(),
            new LayoutAreaReference(AutoSaveTestView));

        // Wait for initial render
        await stream
            .GetControlStream(AutoSaveTestView)
            .Should().Within(10.Seconds()).Match(x => x is not null);

        // Update the title via the client
        stream.UpdatePointer("Auto-saved Title", "/data/\"autosave_test\"", new JsonPointerReference("title"));

        // The server-side subscription debounces and appends to the shared list (not an observable),
        // so probe the list until it reflects the update rather than sleeping for a fixed window.
        await Observable.Interval(50.Milliseconds())
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
    // The canonical write path per AGENTS.md is
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
    public async Task EditState_ShouldSurviveDataUpdates()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = CreateHostAddress();

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(PersistenceView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(PersistenceView)
            .Should().Within(10.Seconds()).Match(x => x is StackControl);

        var stack = control.Should().BeOfType<StackControl>().Subject;

        // Get the first property's stack (Title)
        var titleStackAreaId = stack.Areas.First().Area.ToString()!;
        var titleStack = await layoutStream
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
        await editControlStream.Should().Within(5.Seconds()).Emit();

        Output.WriteLine("Edit mode activated");

        // The entity reaches /data through an asynchronous host-side subscription
        // (workspace stream → host.UpdateData) that syncs to the client independently of
        // the control tree, so the TextField can render before the entity exists in the
        // client's state. UpdatePointer applies a JSON patch against the client's CURRENT
        // state; a patch targeting /data/"persistable_entity"/count before the entity has
        // synced fails with "Target path could not be reached" and is dropped with a
        // warning (by design — fabricating the missing parent would write back a partial
        // entity). Wait for the entity to be present before patching it.
        await layoutStream
            .GetDataStream<PersistableEntity>(new JsonPointerReference("/data/\"persistable_entity\""))
            .Should().Within(5.Seconds()).Match(x => x is not null);

        // While in edit mode, trigger a data update (simulating workspace stream emit)
        Output.WriteLine("Triggering data update while in edit mode...");
        layoutStream.UpdatePointer(42, "/data/\"persistable_entity\"", new JsonPointerReference("count"));

        // Wait for the data update to propagate (count reaches 42) before reading the control state,
        // so we observe the control *after* the re-render rather than sleeping for a fixed window.
        await layoutStream
            .GetDataStream<PersistableEntity>(new JsonPointerReference("/data/\"persistable_entity\""))
            .Should().Within(5.Seconds()).Match(x => x?.Count == 42);

        // Check if still in edit mode (current control after the data-driven re-render)
        var controlAfterDataUpdate = await layoutStream
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
    public async Task Markdown_UsesDoneButton()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var area = workspace.GetStream(new LayoutAreaReference(MarkdownToggleView));

        var control = await area
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

/// <summary>
/// Issue #322: numeric and boolean scalar content fields rendered BLANK in the read-only Overview
/// (visible only after click-to-edit). The read-only numeric branch now pre-stringifies the value —
/// honouring <c>[DisplayFormat]</c> — into a display <c>/data</c> slot, and the client's
/// <c>ConvertJson&lt;string&gt;</c> renders number/bool tokens as text. These tests pin both:
/// numeric values (formatted and raw), nullable-null → empty, boolean read-only, and that clicking
/// a numeric still swaps to a <see cref="NumberFieldControl"/> and round-trips.
/// </summary>
[Collection("ReadonlyNumericRenderingTests")]
public class ReadonlyNumericRenderingTest(ITestOutputHelper output) : HubTestBase(output)
{
    public record NumericEntity
    {
        [Key]
        public string Id { get; init; } = string.Empty;

        [Display(Name = "Amount")]
        [DisplayFormat(DataFormatString = "{0:N1}")]
        public decimal Amount { get; init; }

        [Display(Name = "Page")]
        public int? Page { get; init; }

        [Display(Name = "Empty Page")]
        public int? EmptyPage { get; init; }

        [Display(Name = "Ratio")]
        public double Ratio { get; init; }

        [Display(Name = "Flag")]
        public bool Flag { get; init; }
    }

    // Value chosen so the {0:N1} rendering ("322.8") differs from the stored value (322.844) —
    // proving [DisplayFormat] is applied, not just the raw number.
    private static NumericEntity TestData => new()
    {
        Id = "num-1",
        Amount = 322.844m,
        Page = 6,
        EmptyPage = null,
        Ratio = 2.5,
        Flag = true
    };

    private const string DataId = "numeric_test";
    private const string NumericView = nameof(NumericView);

    private UiControl NumericViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        host.UpdateData(DataId, TestData);

        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));
        foreach (var prop in typeof(NumericEntity).GetProperties())
        {
            var control = host.Hub.ServiceProvider.MapToToggleableControl(prop, DataId, canEdit: true, host);
            grid = grid.WithView(control, s => s.WithXs(12).WithMd(6));
        }
        return grid;
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(NumericView, NumericViewDefinition));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutClient();
    }

    private static string DisplayPointer(string propName)
        => LayoutAreaReference.GetDataPointer($"displayLabel_{DataId}_{propName}");

    /// <summary>
    /// Renders the numeric view on a fresh client and forces every property cell's reactive
    /// read-only view to materialise — a named area (and thus <see cref="BuildFormattedNumberLabel"/>'s
    /// display-slot subscription) only runs once its control stream is subscribed, exactly as the real
    /// Overview subscribes each cell. Returns the client stream to read the resulting display slots.
    /// </summary>
    private async Task<ISynchronizationStream<JsonElement>> RenderNumericView()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(NumericView));

        var control = await stream.GetControlStream(NumericView)
            .Should().Within(10.Seconds()).Match(x => x is LayoutGridControl);
        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        foreach (var area in grid.Areas)
        {
            var cellId = area.Area.ToString()!;
            var cell = await stream.GetControlStream(cellId).Should().Within(5.Seconds()).Match(x => x is not null);
            if (cell is StackControl cellStack && cellStack.Areas.Count > 1)
            {
                var reactiveId = cellStack.Areas.Skip(1).First().Area.ToString()!;
                await stream.GetControlStream(reactiveId).Should().Within(5.Seconds()).Match(x => x is not null);
            }
        }

        return stream;
    }

    [Fact]
    public async Task ReadonlyDecimal_WithDisplayFormat_RendersFormattedValue()
    {
        var stream = await RenderNumericView();

        // [DisplayFormat("{0:N1}")] applied → "322.8" (culture-computed here so the assertion is
        // culture-independent), NOT the raw "322.844" and NOT blank.
        var expected = string.Format("{0:N1}", 322.844m);
        expected.Should().NotBe("322.844");
        await stream
            .GetDataStream<string>(new JsonPointerReference(DisplayPointer("amount")))
            .Should().Within(5.Seconds()).Match(x => x == expected);
    }

    [Fact]
    public async Task ReadonlyNullableInt_WithValue_RendersValue()
    {
        var stream = await RenderNumericView();

        // No [DisplayFormat] → the number's own text; must be the value, not blank.
        await stream
            .GetDataStream<string>(new JsonPointerReference(DisplayPointer("page")))
            .Should().Within(5.Seconds()).Match(x => x == "6");
    }

    [Fact]
    public async Task ReadonlyNullableInt_Null_RendersEmpty()
    {
        var stream = await RenderNumericView();

        // A nullable numeric that is null renders EMPTY, never "0".
        await stream
            .GetDataStream<string>(new JsonPointerReference(DisplayPointer("emptyPage")))
            .Should().Within(5.Seconds()).Match(x => x == "");
    }

    [Fact]
    public async Task ReadonlyDouble_RendersValue()
    {
        var stream = await RenderNumericView();

        await stream
            .GetDataStream<string>(new JsonPointerReference(DisplayPointer("ratio")))
            .Should().Within(5.Seconds()).Match(x => x == "2.5");
    }

    [Fact]
    public async Task ReadonlyBool_RendersItsValue()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(NumericView));

        var control = await stream.GetControlStream(NumericView)
            .Should().Within(10.Seconds()).Match(x => x is LayoutGridControl);

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // The bool ("Flag") is the last property; its toggleable cell is a Stack whose reactive
        // read-only view is a LabelControl bound to the "flag" pointer (not a blank/missing control).
        var flagAreaId = grid.Areas.Last().Area.ToString()!;
        var flagStack = await stream.GetControlStream(flagAreaId)
            .Should().Within(5.Seconds()).Match(x => x is StackControl);
        var reactiveAreaId = flagStack.Should().BeOfType<StackControl>().Subject
            .Areas.Skip(1).First().Area.ToString()!;

        var readonlyView = await stream.GetControlStream(reactiveAreaId)
            .Should().Within(5.Seconds()).Match(x => x is not null);
        // The read-only bool cell wraps a clickable Stack around the LabelControl; unwrap to the label.
        readonlyView.Should().BeOfType<StackControl>();

        // The value the bool label binds to is present (true) — the ConvertJson<string> rendering of
        // that true token to "true" is pinned by LayoutClientExtensionsTest.
        await stream
            .GetDataStream<bool>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(DataId, "flag")))
            .Should().Within(5.Seconds()).Match(x => x);
    }

    [Fact]
    public async Task ClickNumeric_SwitchesToNumberField_AndRoundTrips()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(NumericView));

        var control = await stream.GetControlStream(NumericView)
            .Should().Within(10.Seconds()).Match(x => x is LayoutGridControl);
        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // "Amount" is the second property (after the [Key] Id) → its toggleable cell.
        var amountAreaId = grid.Areas.Skip(1).First().Area.ToString()!;
        var amountStack = await stream.GetControlStream(amountAreaId)
            .Should().Within(5.Seconds()).Match(x => x is StackControl);
        var reactiveAreaId = amountStack.Should().BeOfType<StackControl>().Subject
            .Areas.Skip(1).First().Area.ToString()!;

        // Read-only first.
        await stream.GetControlStream(reactiveAreaId).Should().Within(5.Seconds()).Match(x => x is StackControl);

        var editStream = stream.GetControlStream(reactiveAreaId).Where(x => x is NumberFieldControl);

        // Click → edit control must be a NumberFieldControl (the numeric edit path is untouched).
        client.Post(new ClickedEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));
        var editControl = await editStream.Should().Within(5.Seconds()).Emit();
        editControl.Should().BeOfType<NumberFieldControl>();

        // Round-trip: an edit persists to the bound /data slot.
        stream.UpdatePointer(400.5m, LayoutAreaReference.GetDataPointer(DataId), new JsonPointerReference("amount"));
        var updated = await stream
            .GetDataStream<NumericEntity>(new JsonPointerReference(LayoutAreaReference.GetDataPointer(DataId)))
            .Should().Within(5.Seconds()).Match(x => x != null && x.Amount == 400.5m);
        updated!.Amount.Should().Be(400.5m);
    }
}

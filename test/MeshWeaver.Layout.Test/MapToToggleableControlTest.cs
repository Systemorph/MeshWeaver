using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Messaging;
using Xunit;

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // Should render as a LayoutGridControl
        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;
        grid.Areas.Should().NotBeEmpty();

        // Get the first non-markdown property area (Title)
        var titleAreaId = grid.Areas.Skip(1).First().Area.ToString()!; // Skip Id, get Title

        var titleControl = await area
            .GetControlStream(titleAreaId)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2); // Label + reactive view

        // Get the reactive view area (second area)
        var reactiveAreaId = stack.Areas.Skip(1).First().Area.ToString()!;

        // Initial control should contain a clickable Stack with LabelControl
        var initialControl = await stream
            .GetControlStream(reactiveAreaId)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

        // The initial state should be a clickable Stack
        initialControl.Should().BeOfType<StackControl>();

        // Send click event to switch to edit mode
        client.Post(new ClickedEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for the control to switch to TextField
        var editControl = await stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl)
            .Timeout(5.Seconds())
            .FirstAsync();

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        var reactiveAreaId = stack.Areas.Skip(1).First().Area.ToString()!;

        // Click to enter edit mode
        client.Post(new ClickedEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for edit mode
        var editControl = await stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl)
            .Timeout(5.Seconds())
            .FirstAsync();

        editControl.Should().BeOfType<TextFieldControl>();

        // Send blur event to exit edit mode
        client.Post(new BlurEvent(reactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

        // Wait for the control to switch back to Stack (readonly view)
        var readonlyControl = await stream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is StackControl)
            .Timeout(5.Seconds())
            .FirstAsync();

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        var grid = control.Should().BeOfType<LayoutGridControl>().Subject;

        // Get the Id property area (first property, has [Key] attribute)
        var idAreaId = grid.Areas.First().Area.ToString()!;

        var idControl = await stream
            .GetControlStream(idAreaId)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

        // The Id control should be a Stack
        var idStack = idControl.Should().BeOfType<StackControl>().Subject;

        // Get the reactive view area inside the stack
        if (idStack.Areas.Count > 1)
        {
            var idReactiveAreaId = idStack.Areas.Skip(1).First().Area.ToString()!;

            var idReactiveControl = await stream
                .GetControlStream(idReactiveAreaId)
                .Timeout(5.Seconds())
                .FirstAsync(x => x is not null);

            // Verify that clicking on it does NOT switch to a TextField
            client.Post(new ClickedEvent(idReactiveAreaId, stream.StreamId), o => o.WithTarget(CreateHostAddress()));

            // Wait a bit and verify control is still LabelControl (not TextField)
            await Task.Delay(200, TestContext.Current.CancellationToken);

            var stillReadonly = await stream
                .GetControlStream(idReactiveAreaId)
                .Timeout(5.Seconds())
                .FirstAsync(x => x is not null);

            // Should NOT be a TextFieldControl since [Key] properties are not editable
            stillReadonly.Should().NotBeOfType<TextFieldControl>();
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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // Read initial data from stream
        var initialData = await stream
            .GetDataStream<TestEntity>(new JsonPointerReference("/data/\"binding_test\""))
            .Where(x => x is not null)
            .Timeout(5.Seconds())
            .FirstAsync();

        initialData.Should().NotBeNull();
        initialData!.Title.Should().Be("Test Title");

        // Update the title via the data stream
        stream.UpdatePointer("Updated Title", "/data/\"binding_test\"", new JsonPointerReference("title"));

        // Wait for the update to propagate
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Read updated data
        var updatedData = await stream
            .GetDataStream<TestEntity>(new JsonPointerReference("/data/\"binding_test\""))
            .Where(x => x?.Title == "Updated Title")
            .Timeout(5.Seconds())
            .FirstAsync();

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // Update the title via the client
        stream.UpdatePointer("Auto-saved Title", "/data/\"autosave_test\"", new JsonPointerReference("title"));

        // Wait for debounce and server-side processing
        await Task.Delay(200, TestContext.Current.CancellationToken);

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

        // Set up auto-save: when local data changes, persist via DataChangeRequest
        SetupAutoSave(host, dataId);

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

    private void SetupAutoSave(LayoutAreaHost host, string dataId)
    {
        string? initialJson = null;

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<PersistableEntity>(dataId)
                .Debounce(TimeSpan.FromMilliseconds(100))
                .Subscribe(async entity =>
                {
                    if (entity == null)
                        return;

                    var currentJson = JsonSerializer.Serialize(entity, host.Hub.JsonSerializerOptions);

                    // Skip initial value
                    if (initialJson == null)
                    {
                        initialJson = currentJson;
                        return;
                    }

                    if (currentJson == initialJson)
                        return;

                    Output.WriteLine($"Auto-save: Detected change, persisting entity with Title={entity.Title}");
                    initialJson = currentJson;

                    // Persist via DataChangeRequest
                    await host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(entity),
                        o => o.WithTarget(host.Hub.Address));
                }));
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

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that edits are not persisted.
    ///
    /// The test:
    /// 1. Renders a form with MapToToggleableControl
    /// 2. Updates the Title via UpdatePointer (simulating user edit)
    /// 3. Waits for auto-save debounce
    /// 4. Uses GetDataRequest to get a FRESH instance from the data store
    /// 5. Verifies the Title was persisted
    ///
    /// Expected failure: The fresh instance will still have "Original Title"
    /// because the edit is not properly persisted to the underlying store.
    /// </summary>
    [Fact(Skip = "Demonstrates known limitation: auto-save from layout data section to workspace store is not wired up")]
    public async Task EditAndPersist_StringProperty_ShouldPersistToDataStore()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = CreateHostAddress();

        // Get the layout stream
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(PersistenceView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(PersistenceView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        Output.WriteLine($"Initial control rendered: {control?.GetType().Name}");

        // Verify initial data via workspace stream
        var initialItems = await workspace
            .GetRemoteStream<PersistableEntity>(hostAddress)!
            .Timeout(5.Seconds())
            .FirstAsync();

        var initialEntity = initialItems.FirstOrDefault(e => e.Id == "persist-1");
        initialEntity.Should().NotBeNull();
        initialEntity!.Title.Should().Be("Original Title");
        Output.WriteLine($"Initial entity from stream: Title={initialEntity.Title}");

        // Update the title via UpdatePointer (simulating user edit)
        var newTitle = $"Updated Title {DateTime.Now:HHmmss}";
        Output.WriteLine($"Updating title to: {newTitle}");
        layoutStream.UpdatePointer(newTitle, "/data/\"persistable_entity\"", new JsonPointerReference("title"));

        // Wait for auto-save debounce (100ms) + processing time
        Output.WriteLine("Waiting for auto-save...");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Get FRESH instance from the data store via workspace stream
        // This is the critical check - did the change actually persist?
        var updatedItems = await workspace
            .GetRemoteStream<PersistableEntity>(hostAddress)!
            .Timeout(5.Seconds())
            .FirstAsync();

        var updatedEntity = updatedItems.FirstOrDefault(e => e.Id == "persist-1");

        Output.WriteLine($"Updated entity from stream: Title={updatedEntity?.Title ?? "null"}");

        // THIS ASSERTION SHOULD FAIL - the title should be updated but won't be
        updatedEntity.Should().NotBeNull();
        updatedEntity!.Title.Should().Be(newTitle,
            "The title should be persisted to the data store, but it's not - this demonstrates the bug");
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that DateTime? edits are not persisted.
    /// </summary>
    [Fact(Skip = "Demonstrates known limitation: auto-save from layout data section to workspace store is not wired up")]
    public async Task EditAndPersist_NullableDateTime_ShouldPersistToDataStore()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = CreateHostAddress();

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(PersistenceView));

        // Wait for initial render
        await layoutStream
            .GetControlStream(PersistenceView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        // Verify initial date via workspace stream
        var initialItems = await workspace
            .GetRemoteStream<PersistableEntity>(hostAddress)!
            .Timeout(5.Seconds())
            .FirstAsync();

        var initialEntity = initialItems.FirstOrDefault(e => e.Id == "persist-1");
        initialEntity.Should().NotBeNull();
        initialEntity!.DueDate.Should().Be(new DateTime(2024, 6, 15));
        Output.WriteLine($"Initial DueDate: {initialEntity.DueDate}");

        // Update the due date
        var newDate = new DateTime(2025, 12, 25);
        Output.WriteLine($"Updating DueDate to: {newDate}");
        layoutStream.UpdatePointer(newDate, "/data/\"persistable_entity\"", new JsonPointerReference("dueDate"));

        // Wait for auto-save
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Get fresh instance via workspace stream
        var updatedItems = await workspace
            .GetRemoteStream<PersistableEntity>(hostAddress)!
            .Timeout(5.Seconds())
            .FirstAsync();

        var updatedEntity = updatedItems.FirstOrDefault(e => e.Id == "persist-1");

        Output.WriteLine($"Updated DueDate from stream: {updatedEntity?.DueDate}");

        // THIS ASSERTION SHOULD FAIL
        updatedEntity.Should().NotBeNull();
        updatedEntity!.DueDate.Should().Be(newDate,
            "The DueDate should be persisted to the data store, but it's not - this demonstrates the bug");
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that workspace stream emitting
    /// overwrites local changes, causing data to revert to original values.
    ///
    /// The real scenario: OverviewLayoutArea subscribes to workspace stream and
    /// calls host.UpdateData() when entity changes. If this happens before or
    /// during editing, local changes get overwritten.
    /// </summary>
    [Fact(Skip = "Demonstrates known limitation: workspace stream re-emission overwrites local layout data")]
    public async Task WorkspaceStreamEmit_ShouldNotOverwriteLocalEdits()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = CreateHostAddress();

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(PersistenceView));

        // Wait for initial render
        await layoutStream
            .GetControlStream(PersistenceView)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        // Make a local edit
        var newTitle = "Local Edit Title";
        layoutStream.UpdatePointer(newTitle, "/data/\"persistable_entity\"", new JsonPointerReference("title"));
        Output.WriteLine($"Made local edit: title = {newTitle}");

        // Wait briefly for local update to propagate
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Read the local data to confirm it was updated
        var localDataAfterEdit = await layoutStream
            .GetDataStream<JsonElement>(new JsonPointerReference("/data/\"persistable_entity\""))
            .Where(x => x.ValueKind != JsonValueKind.Undefined)
            .Timeout(5.Seconds())
            .FirstAsync();

        var titleAfterEdit = localDataAfterEdit.TryGetProperty("title", out var t) ? t.GetString() : null;
        Output.WriteLine($"Local data after edit: title = {titleAfterEdit}");
        titleAfterEdit.Should().Be(newTitle, "Local edit should update the data");

        // Now simulate what happens when workspace stream emits (like OverviewLayoutArea does)
        // In the real scenario, the workspace stream subscription would call:
        // host.UpdateData(dataId, serializedEntityFromWorkspace)
        // This would overwrite the local edit with the original value

        // Wait for auto-save debounce to NOT have triggered yet (it's 100ms in our test)
        // Then simulate workspace emitting original data
        await Task.Delay(30, TestContext.Current.CancellationToken); // Still within debounce window

        // Simulate workspace stream emitting original entity data
        // This is what happens when OverviewLayoutArea's subscription receives data
        var originalEntity = InitialData.First();
        layoutStream.UpdatePointer(originalEntity.Title, "/data/\"persistable_entity\"", new JsonPointerReference("title"));
        Output.WriteLine($"Simulated workspace emit: title = {originalEntity.Title}");

        // Now wait for debounce to complete
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Check what gets persisted - should be the LOCAL edit, not the original
        var finalItems = await workspace
            .GetRemoteStream<PersistableEntity>(hostAddress)!
            .Timeout(5.Seconds())
            .FirstAsync();

        var finalEntity = finalItems.FirstOrDefault(e => e.Id == "persist-1");
        Output.WriteLine($"Final persisted title: {finalEntity?.Title}");

        // THIS ASSERTION SHOULD FAIL - the workspace emit overwrote local changes
        finalEntity.Should().NotBeNull();
        finalEntity!.Title.Should().Be(newTitle,
            "Local edits should not be overwritten by workspace stream, but they are - this demonstrates the bug");
    }

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        var stack = control.Should().BeOfType<StackControl>().Subject;

        // Get the first property's stack (Title)
        var titleStackAreaId = stack.Areas.First().Area.ToString()!;
        var titleStack = await layoutStream
            .GetControlStream(titleStackAreaId)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is StackControl);

        var titleStackControl = titleStack.Should().BeOfType<StackControl>().Subject;

        // Get the reactive view area (second child - after the label)
        var reactiveAreaId = titleStackControl.Areas.Skip(1).First().Area.ToString()!;

        // Click to enter edit mode
        Output.WriteLine("Entering edit mode...");
        client.Post(new ClickedEvent(reactiveAreaId, layoutStream.StreamId), o => o.WithTarget(hostAddress));

        // Wait for edit mode
        var editControl = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl)
            .Timeout(5.Seconds())
            .FirstAsync();

        Output.WriteLine("Edit mode activated");

        // While in edit mode, trigger a data update (simulating workspace stream emit)
        Output.WriteLine("Triggering data update while in edit mode...");
        layoutStream.UpdatePointer(42, "/data/\"persistable_entity\"", new JsonPointerReference("count"));

        // Wait for update to propagate
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Check if still in edit mode
        var controlAfterDataUpdate = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Timeout(2.Seconds())
            .FirstAsync();

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
            .Timeout(10.Seconds())
            .FirstAsync(x => x is not null);

        // Should render as a Stack with full width
        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Skin.Should().NotBeNull();
        (stack.Skin as LayoutStackSkin)?.Width.Should().Be("100%");

        // The stack should contain the reactive view area
        stack.Areas.Should().NotBeEmpty();
    }
}

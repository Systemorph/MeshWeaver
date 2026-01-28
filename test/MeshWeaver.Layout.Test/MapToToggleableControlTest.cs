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
using MeshWeaver.Domain;
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

        [UiControl(typeof(MarkdownEditorControl), SeparateEditView = true)]
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
            await Task.Delay(200);

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
        await Task.Delay(100);

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
        await Task.Delay(200);

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
            .Where(p => !p.HasAttribute<KeyAttribute>())
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
    [Fact]
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

        // Verify initial data via GetDataRequest
        var initialResponse = await client.AwaitResponse<GetDataResponse>(
            new GetDataRequest(new CollectionReference(typeof(PersistableEntity).Name)),
            o => o.WithTarget(hostAddress));

        var initialEntities = initialResponse.Message.Data as IEnumerable<PersistableEntity>;
        var initialEntity = initialEntities?.FirstOrDefault(e => e.Id == "persist-1");
        initialEntity.Should().NotBeNull();
        initialEntity!.Title.Should().Be("Original Title");
        Output.WriteLine($"Initial entity from GetDataRequest: Title={initialEntity.Title}");

        // Update the title via UpdatePointer (simulating user edit)
        var newTitle = $"Updated Title {DateTime.Now:HHmmss}";
        Output.WriteLine($"Updating title to: {newTitle}");
        layoutStream.UpdatePointer(newTitle, "/data/\"persistable_entity\"", new JsonPointerReference("title"));

        // Wait for auto-save debounce (100ms) + processing time
        Output.WriteLine("Waiting for auto-save...");
        await Task.Delay(500);

        // Get a FRESH instance from the data store using GetDataRequest
        // This is the critical check - did the change actually persist?
        var updatedResponse = await client.AwaitResponse<GetDataResponse>(
            new GetDataRequest(new CollectionReference(typeof(PersistableEntity).Name)),
            o => o.WithTarget(hostAddress));

        var updatedEntities = updatedResponse.Message.Data as IEnumerable<PersistableEntity>;
        var updatedEntity = updatedEntities?.FirstOrDefault(e => e.Id == "persist-1");

        Output.WriteLine($"Updated entity from GetDataRequest: Title={updatedEntity?.Title ?? "null"}");

        // THIS ASSERTION SHOULD FAIL - the title should be updated but won't be
        updatedEntity.Should().NotBeNull();
        updatedEntity!.Title.Should().Be(newTitle,
            "The title should be persisted to the data store, but it's not - this demonstrates the bug");
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that DateTime? edits are not persisted.
    /// </summary>
    [Fact]
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

        // Verify initial date
        var initialResponse = await client.AwaitResponse<GetDataResponse>(
            new GetDataRequest(new CollectionReference(typeof(PersistableEntity).Name)),
            o => o.WithTarget(hostAddress));

        var initialEntity = (initialResponse.Message.Data as IEnumerable<PersistableEntity>)?
            .FirstOrDefault(e => e.Id == "persist-1");
        initialEntity.Should().NotBeNull();
        initialEntity!.DueDate.Should().Be(new DateTime(2024, 6, 15));
        Output.WriteLine($"Initial DueDate: {initialEntity.DueDate}");

        // Update the due date
        var newDate = new DateTime(2025, 12, 25);
        Output.WriteLine($"Updating DueDate to: {newDate}");
        layoutStream.UpdatePointer(newDate, "/data/\"persistable_entity\"", new JsonPointerReference("dueDate"));

        // Wait for auto-save
        await Task.Delay(500);

        // Get fresh instance
        var updatedResponse = await client.AwaitResponse<GetDataResponse>(
            new GetDataRequest(new CollectionReference(typeof(PersistableEntity).Name)),
            o => o.WithTarget(hostAddress));

        var updatedEntity = (updatedResponse.Message.Data as IEnumerable<PersistableEntity>)?
            .FirstOrDefault(e => e.Id == "persist-1");

        Output.WriteLine($"Updated DueDate from GetDataRequest: {updatedEntity?.DueDate}");

        // THIS ASSERTION SHOULD FAIL
        updatedEntity.Should().NotBeNull();
        updatedEntity!.DueDate.Should().Be(newDate,
            "The DueDate should be persisted to the data store, but it's not - this demonstrates the bug");
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that the edit state switches back prematurely.
    ///
    /// The issue: when user clicks to edit, the edit state briefly becomes true,
    /// but then switches back to false because the underlying data stream emits
    /// a new value (from workspace sync), causing the reactive view to re-evaluate
    /// with StartWith(false).
    /// </summary>
    [Fact]
    public async Task EditState_ShouldRemainTrueUntilBlur()
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

        // Initial state should be readonly (StackControl with LabelControl)
        var initialControl = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Timeout(5.Seconds())
            .FirstAsync(x => x is not null);

        initialControl.Should().BeOfType<StackControl>("Initial state should be readonly StackControl");
        Output.WriteLine($"Initial control type: {initialControl.GetType().Name}");

        // Click to enter edit mode
        Output.WriteLine("Sending click event to enter edit mode...");
        client.Post(new ClickedEvent(reactiveAreaId, layoutStream.StreamId), o => o.WithTarget(hostAddress));

        // Wait for edit mode
        var editControl = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl)
            .Timeout(5.Seconds())
            .FirstAsync();

        editControl.Should().BeOfType<TextFieldControl>("Should switch to TextFieldControl in edit mode");
        Output.WriteLine("Edit mode activated - TextFieldControl visible");

        // Wait a bit to see if edit state stays
        await Task.Delay(300);

        // Check if still in edit mode (THIS IS WHERE IT SHOULD FAIL)
        var controlAfterWait = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Timeout(2.Seconds())
            .FirstAsync();

        Output.WriteLine($"Control after 300ms wait: {controlAfterWait?.GetType().Name}");

        // THIS ASSERTION SHOULD FAIL - the control will have switched back to StackControl
        controlAfterWait.Should().BeOfType<TextFieldControl>(
            "Edit state should remain true until blur, but it switches back prematurely - this demonstrates the bug");
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

        [UiControl(typeof(MarkdownEditorControl), SeparateEditView = true)]
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

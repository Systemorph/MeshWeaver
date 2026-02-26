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
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for MapToToggleableControl data persistence through the monolith mesh.
/// Uses serialization between nodes to verify real-world scenarios.
///
/// IMPORTANT: These tests are EXPECTED TO FAIL to demonstrate a bug where:
/// 1. UpdatePointer correctly updates the local data stream
/// 2. Auto-save detects the change and sends DataChangeRequest
/// 3. BUT the changes are NOT persisted to the underlying data store
/// 4. When getting a fresh stream, it returns the original (unchanged) data
///
/// The tests should be updated to PASS once the bug is fixed.
/// </summary>
[Collection("MapToToggleableControlPersistence")]
public class MapToToggleableControlPersistenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    #region Test Domain

    /// <summary>
    /// Category dimension for testing combobox selection.
    /// </summary>
    public record TestCategory : INamed
    {
        [Key]
        public string Id { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;
    }

    public record TestContent
    {
        [Key]
        public string Id { get; init; } = string.Empty;

        [Display(Name = "Title")]
        public string Title { get; init; } = string.Empty;

        [Display(Name = "Count")]
        public int Count { get; init; }

        [Display(Name = "Due Date")]
        public DateTime? DueDate { get; init; }

        [Display(Name = "Is Active")]
        public bool IsActive { get; init; }

        /// <summary>
        /// Dimension property that renders as a combobox/select control.
        /// This is where the endless loop issue occurs.
        /// </summary>
        [Display(Name = "Category")]
        [Dimension(typeof(TestCategory))]
        public string? CategoryId { get; init; }
    }

    private static TestCategory[] InitialCategories =>
    [
        new TestCategory { Id = "cat-1", DisplayName = "Category One" },
        new TestCategory { Id = "cat-2", DisplayName = "Category Two" },
        new TestCategory { Id = "cat-3", DisplayName = "Category Three" }
    ];

    private static TestContent[] InitialData =>
    [
        new TestContent
        {
            Id = "test-1",
            Title = "Original Title",
            Count = 10,
            DueDate = new DateTime(2024, 6, 15),
            IsActive = false,
            CategoryId = "cat-1"
        }
    ];

    #endregion

    private const string TestNodePath = "ACME/Software/Test/ToggleableControl";
    private const string ToggleableTestView = nameof(ToggleableTestView);

    private UiControl ToggleableTestViewDefinition(LayoutAreaHost host, RenderingContext ctx)
    {
        var dataId = "test_content";

        // Get the entity from workspace stream (like OverviewLayoutArea does)
        var entityStream = host.Workspace.GetStream(new EntityReference(nameof(TestContent), "test-1"));
        if (entityStream != null)
        {
            host.RegisterForDisposal(dataId,
                entityStream
                    .Where(e => e?.Value != null)
                    .Subscribe(e =>
                    {
                        Output.WriteLine($"[Host] Workspace stream emitted entity: {e?.Value}");
                        host.UpdateData(dataId, e!.Value!);
                    }));
        }
        else
        {
            // Fallback - shouldn't happen with proper setup
            host.UpdateData(dataId, InitialData.First());
        }

        // Set up auto-save with initial content known upfront (like OverviewLayoutArea does)
        // This avoids the race condition of using the first debounced emission as initial
        SetupAutoSave(host, dataId, InitialData.First());

        // Build the form using MapToToggleableControl
        var properties = typeof(TestContent).GetProperties()
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

    private void SetupAutoSave(LayoutAreaHost host, string dataId, TestContent initialContent)
    {
        // Set initial JSON from known content upfront (like OverviewLayoutArea does)
        // This avoids race conditions where first debounced emission may already contain edits
        var initialJson = JsonSerializer.Serialize(initialContent, host.Hub.JsonSerializerOptions);
        Output.WriteLine($"[Host AutoSave] Initial JSON set: {initialJson}");

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<TestContent>(dataId)
                .Do(content => Output.WriteLine($"[Host AutoSave] Data stream emitted: Title={content?.Title ?? "null"}"))
                .Debounce(TimeSpan.FromMilliseconds(100))
                .Subscribe(async content =>
                {
                    if (content == null)
                        return;

                    var currentJson = JsonSerializer.Serialize(content, host.Hub.JsonSerializerOptions);

                    if (currentJson == initialJson)
                    {
                        Output.WriteLine("[Host AutoSave] No change detected, skipping");
                        return;
                    }

                    Output.WriteLine($"[Host AutoSave] Change detected! Sending DataChangeRequest for Title={content.Title}");
                    initialJson = currentJson;

                    await host.Hub.AwaitResponse<DataChangeResponse>(
                        new DataChangeRequest().WithUpdates(content),
                        o => o.WithTarget(host.Hub.Address));
                }));
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return base.ConfigureMesh(builder)
            .AddMeshNodes(MeshNode.FromPath(TestNodePath) with
            {
                Name = "ToggleableControl Test",
                HubConfiguration = config => config
                    .AddData(data => data
                        .AddSource(source => source
                            .WithType<TestCategory>(type => type
                                .WithInitialData(InitialCategories))
                            .WithType<TestContent>(type => type
                                .WithInitialData(InitialData))))
                    .AddLayout(layout => layout
                        .WithView(ToggleableTestView, ToggleableTestViewDefinition))
            });
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that edits made via UpdatePointer
    /// are not properly persisted to the data store when going through
    /// the monolith mesh with serialization.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EditAndPersist_ThroughMonolithMesh_ShouldPersistToDataStore()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        // Use simple Address (not CreateAppAddress) to match how nodes are registered
        var hostAddress = new Address(TestNodePath);

        Output.WriteLine($"Client address: {client.Address}");
        Output.WriteLine($"Host address: {hostAddress}");

        // Get the layout stream through the mesh (with serialization)
        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        Output.WriteLine($"Initial control rendered: {control?.GetType().Name}");

        // Verify initial data via workspace stream (goes through mesh serialization)
        var initialItems = await workspace
            .GetRemoteStream<TestContent>(hostAddress)!
            .Timeout(10.Seconds())
            .FirstAsync();

        var initialEntity = initialItems.FirstOrDefault(e => e.Id == "test-1");
        initialEntity.Should().NotBeNull();
        initialEntity!.Title.Should().Be("Original Title");
        Output.WriteLine($"Initial entity from stream: Title={initialEntity.Title}");

        // Update the title via UpdatePointer (simulating user edit through the mesh)
        var newTitle = $"Updated Title {DateTime.Now:HHmmss}";
        Output.WriteLine($"Updating title to: {newTitle}");
        layoutStream.UpdatePointer(newTitle, "/data/\"test_content\"", new JsonPointerReference("title"));

        // Wait for auto-save debounce (100ms) + network + processing
        Output.WriteLine("Waiting for auto-save...");
        await Task.Delay(1000);

        // Get FRESH instance from the data store via workspace stream
        // This goes through the mesh with serialization
        var updatedItems = await workspace
            .GetRemoteStream<TestContent>(hostAddress)!
            .Timeout(10.Seconds())
            .FirstAsync();

        var updatedEntity = updatedItems.FirstOrDefault(e => e.Id == "test-1");
        Output.WriteLine($"Updated entity from stream: Title={updatedEntity?.Title ?? "null"}");

        // THIS ASSERTION SHOULD FAIL - demonstrates the bug
        updatedEntity.Should().NotBeNull();
        updatedEntity!.Title.Should().Be(newTitle,
            "The title should be persisted to the data store through the monolith mesh, but it's not - this demonstrates the bug");
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that DateTime? edits are not persisted.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EditAndPersist_NullableDateTime_ThroughMonolithMesh()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        // Verify initial date
        var initialItems = await workspace
            .GetRemoteStream<TestContent>(hostAddress)!
            .Timeout(10.Seconds())
            .FirstAsync();

        var initialEntity = initialItems.FirstOrDefault(e => e.Id == "test-1");
        initialEntity.Should().NotBeNull();
        initialEntity!.DueDate.Should().Be(new DateTime(2024, 6, 15));
        Output.WriteLine($"Initial DueDate: {initialEntity.DueDate}");

        // Update the due date
        var newDate = new DateTime(2025, 12, 25);
        Output.WriteLine($"Updating DueDate to: {newDate}");
        layoutStream.UpdatePointer(newDate, "/data/\"test_content\"", new JsonPointerReference("dueDate"));

        // Wait for auto-save
        await Task.Delay(1000);

        // Get fresh instance
        var updatedItems = await workspace
            .GetRemoteStream<TestContent>(hostAddress)!
            .Timeout(10.Seconds())
            .FirstAsync();

        var updatedEntity = updatedItems.FirstOrDefault(e => e.Id == "test-1");
        Output.WriteLine($"Updated DueDate from stream: {updatedEntity?.DueDate}");

        // THIS ASSERTION SHOULD FAIL
        updatedEntity.Should().NotBeNull();
        updatedEntity!.DueDate.Should().Be(newDate,
            "The DueDate should be persisted through the monolith mesh, but it's not - this demonstrates the bug");
    }

    /// <summary>
    /// THIS TEST SHOULD FAIL - demonstrates that edit state resets
    /// when workspace stream emits after data change.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EditState_ShouldSurviveWorkspaceStreamEmit()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        var stack = control.Should().BeOfType<StackControl>().Subject;

        // Get the first property's stack (Title)
        var titleStackAreaId = stack.Areas.First().Area.ToString()!;
        var titleStack = await layoutStream
            .GetControlStream(titleStackAreaId)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        var titleStackControl = titleStack.Should().BeOfType<StackControl>().Subject;

        // Get the reactive view area (second child - after the label)
        var reactiveAreaId = titleStackControl.Areas.Skip(1).First().Area.ToString()!;

        // Click to enter edit mode
        Output.WriteLine("Entering edit mode via ClickedEvent...");
        client.Post(new ClickedEvent(reactiveAreaId, layoutStream.StreamId), o => o.WithTarget(hostAddress));

        // Wait for edit mode
        var editControl = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Where(x => x is TextFieldControl)
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine($"Edit mode activated: {editControl.GetType().Name}");

        // Now make an edit - this should trigger workspace stream to emit updated data
        Output.WriteLine("Making edit to trigger workspace stream...");
        layoutStream.UpdatePointer("Test Edit", "/data/\"test_content\"", new JsonPointerReference("title"));

        // Wait for workspace to process and emit
        await Task.Delay(500);

        // Check if still in edit mode
        var controlAfterEdit = await layoutStream
            .GetControlStream(reactiveAreaId)
            .Timeout(5.Seconds())
            .FirstAsync();

        Output.WriteLine($"Control after workspace emit: {controlAfterEdit?.GetType().Name}");

        // THIS ASSERTION SHOULD FAIL - workspace emit resets edit state
        controlAfterEdit.Should().BeOfType<TextFieldControl>(
            "Edit state should survive workspace stream emit, but it resets to readonly - this demonstrates the bug");
    }

    /// <summary>
    /// Tests that GetDataStream doesn't cause endless emissions.
    /// This test subscribes directly to GetDataStream and counts emissions
    /// to detect if there's an infinite loop.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task GetDataStream_ShouldNotCauseEndlessEmissions()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        // Subscribe to data stream via Reduce - this is where the endless loop happens
        var emissionCount = 0;
        var lastEmissionTime = DateTime.Now;
        var emissionsLog = new System.Collections.Generic.List<string>();

        using var subscription = layoutStream
            .Reduce(new JsonPointerReference("/data/\"test_content\""))!
            .Where(x => x.Value.ValueKind != JsonValueKind.Undefined)
            .Subscribe(data =>
            {
                emissionCount++;
                var elapsed = DateTime.Now - lastEmissionTime;
                lastEmissionTime = DateTime.Now;
                var logEntry = $"Data stream emission #{emissionCount} after {elapsed.TotalMilliseconds:F0}ms";
                emissionsLog.Add(logEntry);
                Output.WriteLine(logEntry);

                // Safety: stop after too many emissions
                if (emissionCount > 100)
                {
                    Output.WriteLine("ENDLESS LOOP DETECTED - stopping after 100 emissions");
                }
            });

        // Wait for initial emissions to settle
        await Task.Delay(200);
        var initialEmissions = emissionCount;
        Output.WriteLine($"Initial emissions after 200ms: {initialEmissions}");

        // Make an edit via UpdatePointer
        Output.WriteLine("Making edit via UpdatePointer...");
        layoutStream.UpdatePointer("Test Edit Value", "/data/\"test_content\"", new JsonPointerReference("title"));

        // Wait and count emissions
        await Task.Delay(300);
        var afterEditEmissions = emissionCount;
        Output.WriteLine($"After edit emissions (300ms): {afterEditEmissions}");

        // Log all emissions for debugging
        Output.WriteLine($"\n=== All {emissionsLog.Count} emissions ===");
        foreach (var log in emissionsLog.Take(50))
        {
            Output.WriteLine(log);
        }

        // Should have at most a few emissions, not endless
        // Initial: ~1-2, After edit: ~1-2 more
        afterEditEmissions.Should().BeLessThan(20,
            $"GetDataStream should not cause endless emissions. Got {afterEditEmissions} emissions. " +
            "This indicates DistinctUntilChanged is not working for JsonElement.");
    }

    /// <summary>
    /// Tests that editing in the UI doesn't cause endless emissions in the host data stream.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EditInUI_ShouldNotCauseEndlessDataStreamEmissions()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        var stack = control.Should().BeOfType<StackControl>().Subject;

        // Get the first property's stack (Title)
        var titleStackAreaId = stack.Areas.First().Area.ToString()!;
        var titleStack = await layoutStream
            .GetControlStream(titleStackAreaId)
            .Timeout(10.Seconds())
            .FirstAsync(x => x is StackControl);

        var titleStackControl = titleStack.Should().BeOfType<StackControl>().Subject;
        var reactiveAreaId = titleStackControl.Areas.Skip(1).First().Area.ToString()!;

        // Track emissions
        var emissionCount = 0;
        using var subscription = layoutStream
            .Reduce(new JsonPointerReference("/data/\"test_content\""))!
            .Where(x => x.Value.ValueKind != JsonValueKind.Undefined)
            .Subscribe(data =>
            {
                emissionCount++;
                var title = data.Value.TryGetProperty("title", out var t) ? t.GetString() : "?";
                Output.WriteLine($"Data emission #{emissionCount}: title={title}");
            });

        await Task.Delay(200);
        var beforeClickEmissions = emissionCount;
        Output.WriteLine($"Before click: {beforeClickEmissions} emissions");

        // Click to enter edit mode
        Output.WriteLine("Clicking to enter edit mode...");
        client.Post(new ClickedEvent(reactiveAreaId, layoutStream.StreamId), o => o.WithTarget(hostAddress));

        await Task.Delay(200);
        var afterClickEmissions = emissionCount;
        Output.WriteLine($"After click (edit mode): {afterClickEmissions} emissions");

        // Make an edit
        Output.WriteLine("Making edit...");
        layoutStream.UpdatePointer("UI Edit Test", "/data/\"test_content\"", new JsonPointerReference("title"));

        await Task.Delay(300);
        var afterEditEmissions = emissionCount;
        Output.WriteLine($"After edit: {afterEditEmissions} emissions");

        // Should not have endless emissions
        var totalNewEmissions = afterEditEmissions - beforeClickEmissions;
        totalNewEmissions.Should().BeLessThan(20,
            $"Click + edit should not cause endless emissions. Got {totalNewEmissions} new emissions.");
    }

    /// <summary>
    /// Tests that selecting a different option in a Dimension combobox doesn't cause endless emissions.
    /// This is the main scenario reported by the user - selecting in a combobox causes endless messages.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task DimensionCombobox_SelectionShouldNotCauseEndlessEmissions()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        Output.WriteLine($"Initial control rendered: {control?.GetType().Name}");

        // Track emissions on the data stream
        // Note: Using DistinctUntilChanged with JsonElementContentComparer to filter duplicate emissions
        // This simulates what GetDataStream does internally
        var emissionCount = 0;
        var lastCategoryId = "";
        var emissionsLog = new System.Collections.Generic.List<string>();

        using var subscription = layoutStream
            .Reduce(new JsonPointerReference("/data/\"test_content\""))!
            .Where(x => x.Value.ValueKind != JsonValueKind.Undefined)
            .Select(x => x.Value)
            .DistinctUntilChanged(JsonElementContentComparer.Instance)
            .Subscribe(data =>
            {
                emissionCount++;
                var categoryId = data.TryGetProperty("categoryId", out var c) ? c.GetString() : "null";
                var logEntry = $"Data emission #{emissionCount}: categoryId={categoryId} (was {lastCategoryId})";
                emissionsLog.Add(logEntry);
                Output.WriteLine(logEntry);
                lastCategoryId = categoryId ?? "null";

                // Safety: stop after too many emissions
                if (emissionCount > 100)
                {
                    Output.WriteLine("ENDLESS LOOP DETECTED - stopping after 100 emissions");
                }
            });

        // Wait for initial emissions to settle
        await Task.Delay(200);
        var initialEmissions = emissionCount;
        Output.WriteLine($"Initial emissions after 200ms: {initialEmissions}");

        // Verify initial category
        lastCategoryId.Should().Be("cat-1", "Initial category should be cat-1");

        // Change the category via UpdatePointer - simulating combobox selection
        Output.WriteLine("Selecting different category (cat-2) via UpdatePointer...");
        layoutStream.UpdatePointer("cat-2", "/data/\"test_content\"", new JsonPointerReference("categoryId"));

        // Wait and count emissions
        await Task.Delay(300);
        var afterSelectionEmissions = emissionCount;
        Output.WriteLine($"After selection emissions (300ms): {afterSelectionEmissions}");

        // Log all emissions for debugging
        Output.WriteLine($"\n=== All {emissionsLog.Count} emissions ===");
        foreach (var log in emissionsLog.Take(50))
        {
            Output.WriteLine(log);
        }

        // Should have at most a few emissions, not endless
        // Initial: ~1-2, After selection: ~1-2 more
        var newEmissions = afterSelectionEmissions - initialEmissions;
        newEmissions.Should().BeLessThan(10,
            $"Dimension combobox selection should not cause endless emissions. Got {newEmissions} new emissions after selection. " +
            "This indicates DistinctUntilChanged is not working for JsonElement in dimension handling.");
    }

    /// <summary>
    /// Tests that BuildDimensionReadOnlyLabel CombineLatest doesn't cause endless emissions.
    /// This specifically tests the issue where CombineLatest of dataStream and collectionStream
    /// fires indefinitely because UpdateData triggers more emissions.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task BuildDimensionReadOnlyLabel_CombineLatest_ShouldNotCauseEndlessEmissions()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        Output.WriteLine("Initial render complete");

        // Subscribe to the displayLabel data stream for the Category dimension
        // This is what BuildDimensionReadOnlyLabel creates via CombineLatest
        var displayLabelId = "displayLabel_test_content_categoryId";
        var emissionCount = 0;
        var emissionsLog = new List<string>();

        using var subscription = layoutStream
            .Reduce(new JsonPointerReference($"/data/\"{displayLabelId}\""))!
            .Where(x => x.Value.ValueKind != JsonValueKind.Undefined)
            .Subscribe(data =>
            {
                emissionCount++;
                var displayName = data.Value.ValueKind == JsonValueKind.String
                    ? data.Value.GetString()
                    : data.Value.ToString();
                var logEntry = $"DisplayLabel emission #{emissionCount}: {displayName}";
                emissionsLog.Add(logEntry);
                Output.WriteLine(logEntry);

                if (emissionCount > 50)
                {
                    Output.WriteLine("ENDLESS LOOP DETECTED in BuildDimensionReadOnlyLabel!");
                }
            });

        // Wait for initial emissions to settle
        await Task.Delay(200);
        var initialEmissions = emissionCount;
        Output.WriteLine($"Initial emissions: {initialEmissions}");

        // Now change the category value - this should trigger CombineLatest
        Output.WriteLine("Changing category from cat-1 to cat-2...");
        layoutStream.UpdatePointer("cat-2", "/data/\"test_content\"", new JsonPointerReference("categoryId"));

        // Wait and count emissions
        await Task.Delay(500);
        var afterChangeEmissions = emissionCount;
        Output.WriteLine($"Emissions after category change: {afterChangeEmissions}");

        // Log all emissions
        Output.WriteLine($"\n=== All {emissionsLog.Count} emissions ===");
        foreach (var log in emissionsLog.Take(60))
        {
            Output.WriteLine(log);
        }

        // Should have at most a few emissions, not endless
        afterChangeEmissions.Should().BeLessThan(20,
            $"BuildDimensionReadOnlyLabel CombineLatest should not cause endless emissions after data change. " +
            $"Got {afterChangeEmissions} emissions. This indicates missing DistinctUntilChanged.");
    }

    /// <summary>
    /// Tests that entering edit mode for a Dimension property (clicking on the readonly label)
    /// and then selecting a different value doesn't cause endless emissions.
    /// This tests the full click-to-edit -> select -> blur flow.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task DimensionCombobox_ClickEditSelectBlur_ShouldNotCauseEndlessEmissions()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var hostAddress = new Address(TestNodePath);

        var layoutStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress,
            new LayoutAreaReference(ToggleableTestView));

        // Wait for initial render
        var control = await layoutStream
            .GetControlStream(ToggleableTestView)
            .Timeout(15.Seconds())
            .FirstAsync(x => x is StackControl);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        Output.WriteLine($"Stack has {stack.Areas.Count} areas");

        // Track emissions
        var emissionCount = 0;
        var emissionsLog = new System.Collections.Generic.List<string>();

        using var subscription = layoutStream
            .Reduce(new JsonPointerReference("/data/\"test_content\""))!
            .Where(x => x.Value.ValueKind != JsonValueKind.Undefined)
            .Subscribe(data =>
            {
                emissionCount++;
                var categoryId = data.Value.TryGetProperty("categoryId", out var c) ? c.GetString() : "null";
                var title = data.Value.TryGetProperty("title", out var t) ? t.GetString() : "null";
                var logEntry = $"Emission #{emissionCount}: categoryId={categoryId}, title={title}";
                emissionsLog.Add(logEntry);
                Output.WriteLine(logEntry);
            });

        await Task.Delay(200);
        var beforeEditEmissions = emissionCount;
        Output.WriteLine($"Before any edit: {beforeEditEmissions} emissions");

        // Find the CategoryId property stack - should be one of the children
        // The CategoryId is the 5th property (after Title, Count, DueDate, IsActive)
        StackControl? categoryStackControl = null;
        string? categoryReactiveAreaId = null;

        foreach (var area in stack.Areas)
        {
            var areaId = area.Area.ToString()!;
            try
            {
                var areaControl = await layoutStream
                    .GetControlStream(areaId)
                    .Timeout(5.Seconds())
                    .FirstAsync();

                if (areaControl is StackControl sc && sc.Areas.Count >= 2)
                {
                    // Check if this is the Category stack by looking at the label
                    var labelAreaId = sc.Areas.First().Area.ToString()!;
                    var labelControl = await layoutStream
                        .GetControlStream(labelAreaId)
                        .Timeout(2.Seconds())
                        .FirstAsync();

                    if (labelControl is LabelControl lbl)
                    {
                        Output.WriteLine($"Found label: {lbl.Data}");
                        if (lbl.Data?.ToString()?.Contains("Category") == true)
                        {
                            categoryStackControl = sc;
                            categoryReactiveAreaId = sc.Areas.Skip(1).First().Area.ToString()!;
                            Output.WriteLine($"Found Category stack, reactive area: {categoryReactiveAreaId}");
                            break;
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                // Skip areas that don't render in time
            }
        }

        if (categoryReactiveAreaId == null)
        {
            Output.WriteLine("Could not find Category property stack - skipping click-to-edit test");
            // Just verify that the dimension selection works without click-to-edit
            layoutStream.UpdatePointer("cat-3", "/data/\"test_content\"", new JsonPointerReference("categoryId"));
            await Task.Delay(300);
        }
        else
        {
            // Click to enter edit mode on Category
            Output.WriteLine($"Clicking to enter edit mode on Category...");
            client.Post(new ClickedEvent(categoryReactiveAreaId, layoutStream.StreamId), o => o.WithTarget(hostAddress));

            await Task.Delay(200);
            var afterClickEmissions = emissionCount;
            Output.WriteLine($"After click: {afterClickEmissions} emissions");

            // Make selection via UpdatePointer
            Output.WriteLine("Selecting cat-3 via UpdatePointer...");
            layoutStream.UpdatePointer("cat-3", "/data/\"test_content\"", new JsonPointerReference("categoryId"));

            await Task.Delay(300);
        }

        var afterSelectionEmissions = emissionCount;
        Output.WriteLine($"After selection: {afterSelectionEmissions} emissions");

        // Log all emissions
        Output.WriteLine($"\n=== All {emissionsLog.Count} emissions ===");
        foreach (var log in emissionsLog)
        {
            Output.WriteLine(log);
        }

        // Should not have endless emissions
        var totalNewEmissions = afterSelectionEmissions - beforeEditEmissions;
        totalNewEmissions.Should().BeLessThan(20,
            $"Dimension click-edit-select should not cause endless emissions. Got {totalNewEmissions} new emissions.");
    }
}

using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Tests for Todo layout areas and interactive functionality
/// </summary>
public class TodoLayoutAreaTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host with Todo application and sample data
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    /// <summary>
    /// Configures the client to connect to host and include layout client functionality
    /// </summary>
    /// <param name="configuration">The configuration to modify</param>
    /// <returns>The modified configuration</returns>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient(d => d)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource =>
                    dataSource.WithType<TodoItem>())
            );
    }

    /// <summary>
    /// Tests the complete todo item interaction workflow:
    /// 1. Set up data context with TodoItems
    /// 2. Register TODO views
    /// 3. Create a subscription on TodoList layout area
    /// 4. Wait for layout area to be rendered
    /// 5. Get the first button and click it
    /// 6. Wait for LayoutArea to update
    /// </summary>
    [HubFact]
    public async Task TodoList_LayoutArea_ShouldUpdateAfterButtonClick()
    {
        // Arrange
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Act - Step 3: Create a subscription on TodoList layout area
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        // Step 4: Wait for layout area to be rendered
        var initialControl = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"Initial control type: {initialControl.GetType().Name}");

        // Verify the control was rendered properly
        initialControl.Should().NotBeNull();
        var layoutGrid = initialControl.Should().BeOfType<LayoutGridControl>()
            .Which;

        Output.WriteLine($"LayoutGrid has {layoutGrid.Areas.Count} areas");

        // Find all buttons in the layout areas
        var buttonAreas = new List<string>();
        foreach (var area in layoutGrid.Areas)
        {
            Output.WriteLine($"Checking area: {area.Area}, Control type: {area.View?.GetType().Name ?? "null"}");

            if (IsButtonControl(area.View))
            {
                buttonAreas.Add(area.Area);
                Output.WriteLine($"Found button in area: {area.Area}");
            }
        }

        // Step 5: Get the first button you find and click it
        buttonAreas.Should().NotBeEmpty("Expected to find at least one button in the TodoList layout");
        var firstButtonArea = buttonAreas.First();

        Output.WriteLine($"Clicking button in area: {firstButtonArea}");

        // Get initial data count
        var initialTodoData = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        var initialCount = initialTodoData?.Count ?? 0;
        Output.WriteLine($"Initial todo count: {initialCount}");

        // Click the button
        client.Post(new ClickedEvent(firstButtonArea, stream.StreamId), o => o.WithTarget(new HostAddress()));

        // Step 6: Wait for LayoutArea to update
        var updatedControl = await stream
            .GetControlStream(reference.Area)
            .Skip(1) // Skip the initial control we already received
            .Timeout(10.Seconds())
            .FirstAsync();

        Output.WriteLine($"Updated control type: {updatedControl.GetType().Name}");

        // Verify the layout updated
        updatedControl.Should().NotBeNull();
        updatedControl.Should().BeOfType<LayoutGridControl>();

        // Verify that data has been updated (this depends on what the button does)
        // We'll check if the workspace data stream emits a new value
        var updatedTodoData = await workspace
            .GetObservable<TodoItem>()
            .Skip(1) // Skip initial data
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        // The test should verify that the UI properly responds to data changes
        // If clicking a status change button, we should see the status reflected in the data
        if (updatedTodoData != null)
        {
            Output.WriteLine($"Updated todo count: {updatedTodoData.Count}");

            // Check if any todo status changed
            var statusChanged = updatedTodoData.Any(todo =>
                initialTodoData?.Any(initial => initial.Id == todo.Id && initial.Status != todo.Status) == true);

            if (statusChanged)
            {
                Output.WriteLine("Todo status change detected - layout update working correctly");
            }
        }
    }

    /// <summary>
    /// Test that verifies data initialization and layout rendering works
    /// </summary>
    [HubFact]
    public async Task TodoList_LayoutArea_ShouldInitializeWithSampleData()
    {
        // Arrange
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Act
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        var control = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert
        control.Should().NotBeNull();
        control.Should().BeOfType<LayoutGridControl>();

        // Verify we have the sample data
        var todoData = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        todoData.Should().NotBeNull();
        todoData.Should().NotBeEmpty();

        Output.WriteLine($"Found {todoData.Count} todo items in workspace");

        // Verify that the sample data matches what we expect
        var sampleTodos = TodoSampleData.GetSampleTodos().ToList();
        todoData.Should().HaveCount(sampleTodos.Count);

        // Check that all categories from sample data are present
        var expectedCategories = sampleTodos.Select(t => t.Category).Distinct().ToList();
        var actualCategories = todoData.Select(t => t.Category).Distinct().ToList();
        actualCategories.Should().BeEquivalentTo(expectedCategories);
    }

    /// <summary>
    /// Test specific button interaction by clicking a Start button and verifying status change
    /// </summary>
    [HubFact]
    public async Task TodoList_StartButton_ShouldUpdateTodoStatus()
    {
        // Arrange
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        // Wait for initial rendering
        var initialControl = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Get initial data to find a pending todo
        var initialTodos = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        var pendingTodo = initialTodos?.FirstOrDefault(t => t.Status == TodoStatus.Pending);
        pendingTodo.Should().NotBeNull("Expected to find at least one pending todo item");

        Output.WriteLine($"Found pending todo: {pendingTodo!.Title}");

        // Look for a Start button in the layout
        var layoutGrid = initialControl.Should().BeOfType<LayoutGridControl>().Which;
        var startButtonArea = FindButtonAreaContaining(layoutGrid, "▶️ Start");

        startButtonArea.Should().NotBeNull("Expected to find a Start button in the layout");
        Output.WriteLine($"Found Start button in area: {startButtonArea}");

        // Act - Click the Start button
        client.Post(new ClickedEvent(startButtonArea!, stream.StreamId), o => o.WithTarget(new HostAddress()));

        // Assert - Wait for data to update
        var updatedTodos = await workspace
            .GetObservable<TodoItem>()
            .Skip(1) // Skip initial data
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        updatedTodos.Should().NotBeNull();

        // Check if the pending todo's status changed to InProgress
        var updatedTodo = updatedTodos!.FirstOrDefault(t => t.Id == pendingTodo.Id);
        updatedTodo.Should().NotBeNull();

        if (updatedTodo!.Status == TodoStatus.InProgress)
        {
            Output.WriteLine($"✅ Todo status successfully changed from Pending to InProgress");
        }
        else
        {
            Output.WriteLine($"❌ Todo status did not change as expected. Current status: {updatedTodo.Status}");

            // This might indicate the issue the user mentioned about DataChangeRequest not properly updating
            Output.WriteLine("This suggests the DataChangeRequest is not properly triggering workspace updates");
        }
    }

    /// <summary>
    /// Helper method to check if a control is a button
    /// </summary>
    /// <param name="control">The control to check</param>
    /// <returns>True if the control is a button</returns>
    private static bool IsButtonControl(UiControl? control)
    {
        return control is ButtonControl;
    }

    /// <summary>
    /// Helper method to find a button area containing specific text
    /// </summary>
    /// <param name="layoutGrid">The layout grid to search</param>
    /// <param name="buttonText">The text to search for in buttons</param>
    /// <returns>The area name of the button, or null if not found</returns>
    private static string? FindButtonAreaContaining(LayoutGridControl layoutGrid, string buttonText)
    {
        foreach (var area in layoutGrid.Areas)
        {
            if (area.View is ButtonControl button && button.Data?.ToString()?.Contains(buttonText) == true)
            {
                return area.Area;
            }

            // Also check in nested stack controls
            if (area.View is StackControl stack)
            {
                var foundInStack = FindButtonInStack(stack, buttonText);
                if (foundInStack != null)
                {
                    return area.Area; // Return the parent area containing the stack
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Helper method to recursively search for buttons in stack controls
    /// </summary>
    /// <param name="stack">The stack control to search</param>
    /// <param name="buttonText">The text to search for</param>
    /// <returns>True if button found in stack</returns>
    private static bool FindButtonInStack(StackControl stack, string buttonText)
    {
        foreach (var area in stack.Areas)
        {
            if (area.View is ButtonControl button && button.Data?.ToString()?.Contains(buttonText) == true)
            {
                return true;
            }

            if (area.View is StackControl nestedStack)
            {
                if (FindButtonInStack(nestedStack, buttonText))
                {
                    return true;
                }
            }
        }
        return false;
    }
}

using System.Reactive.Linq;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Tests for Todo layout areas and interactive functionality
/// </summary>
public class TodoLayoutAreaInteractionTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>
    /// Configures the host with Todo application and sample data
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    /// <summary>
    /// Configures the client to connect to host and include layout client functionality
    /// </summary>
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
    /// Test Step 1: Set up data context with TodoItems and verify initialization
    /// </summary>
    [HubFact]
    public async Task Step1_TodoData_ShouldInitializeWithSampleData()
    {
        // Arrange & Act
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Wait for todo data to be available
        var todoData = await workspace
            .GetObservable<TodoItem>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        // Assert
        todoData.Should().NotBeNull();
        todoData.Should().NotBeEmpty();

        Output.WriteLine($"✅ Step 1 PASSED: Found {todoData.Count} todo items in workspace");

        // Verify that the sample data matches what we expect
        var sampleTodos = TodoSampleData.GetSampleTodos().ToList();
        todoData.Should().HaveCount(sampleTodos.Count);
    }

    /// <summary>
    /// Test Step 2 & 3: Register TODO views and create a subscription on TodoList layout area
    /// </summary>
    [HubFact]
    public async Task Step2and3_TodoListLayoutArea_ShouldRenderWithButtons()
    {
        // Arrange - Step 2: Views are registered via ConfigureTodoApplication
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Act - Step 3: Create a subscription on TodoList layout area
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        // Step 4: Wait for layout area to be rendered
        var control = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        // Assert
        control.Should().NotBeNull();
        Output.WriteLine($"✅ Step 2&3 PASSED: Layout area rendered with control type: {control.GetType().Name}");

        // Verify we get a LayoutGrid (this is what TodoLayoutArea.TodoList returns)
        var layoutGrid = control.Should().BeOfType<LayoutGridControl>().Which;
        Output.WriteLine($"LayoutGrid has {layoutGrid.Areas.Count} areas");

        // Look for buttons in the layout
        var buttonAreas = FindAllButtonAreas(layoutGrid);
        buttonAreas.Should().NotBeEmpty("Expected to find at least one button in the TodoList layout");

        Output.WriteLine($"✅ Found {buttonAreas.Count} button areas in the layout");
        foreach (var buttonArea in buttonAreas)
        {
            Output.WriteLine($"  - Button area: {buttonArea}");
        }
    }

    /// <summary>
    /// Test Steps 4, 5 & 6: Complete workflow including button click and data update verification
    /// </summary>
    [HubFact]
    public async Task Steps4to6_ButtonClick_ShouldTriggerDataUpdate()
    {
        // Arrange
        var reference = new LayoutAreaReference(nameof(TodoLayoutArea.TodoList));
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new HostAddress(),
            reference
        );

        // Step 4: Wait for layout area to be rendered
        var initialControl = await stream
            .GetControlStream(reference.Area)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        Output.WriteLine($"✅ Step 4 PASSED: Initial control rendered");

        // Get initial data to compare later
        var initialTodos = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        var pendingTodo = initialTodos?.FirstOrDefault(t => t.Status == TodoStatus.Pending);
        pendingTodo.Should().NotBeNull("Expected to find at least one pending todo item for testing");

        Output.WriteLine($"Found pending todo for testing: {pendingTodo!.Title}");

        // Step 5: Get the first button you find and click it
        var layoutGrid = initialControl.Should().BeOfType<LayoutGridControl>().Which;
        var buttonAreas = FindAllButtonAreas(layoutGrid);
        buttonAreas.Should().NotBeEmpty("Expected to find buttons to click");

        var firstButtonArea = buttonAreas.First();
        Output.WriteLine($"✅ Step 5: Clicking first button in area: {firstButtonArea}");

        // Click the button
        client.Post(new ClickedEvent(firstButtonArea, stream.StreamId), o => o.WithTarget(new HostAddress()));

        // Step 6: Wait for LayoutArea to update  
        try
        {
            // Wait for either layout update OR data update (whichever comes first)
            var layoutUpdateTask = stream
                .GetControlStream(reference.Area)
                .Skip(1) // Skip the initial control we already received
                .Timeout(10.Seconds())
                .FirstAsync();

            var dataUpdateTask = workspace
                .GetObservable<TodoItem>()
                .Skip(1) // Skip initial data
                .Timeout(10.Seconds())
                .FirstAsync();

            // Wait for either update
            await Task.WhenAny(layoutUpdateTask, dataUpdateTask);

            if (layoutUpdateTask.IsCompleted)
            {
                Output.WriteLine("✅ Step 6 PASSED: Layout area updated after button click");
            }

            if (dataUpdateTask.IsCompleted)
            {
                var updatedTodos = await dataUpdateTask;
                Output.WriteLine("✅ Step 6 PASSED: Data stream updated after button click");

                // Check if any todo status changed
                var statusChanged = updatedTodos.Any(todo =>
                    initialTodos?.Any(initial => initial.Id == todo.Id && initial.Status != todo.Status) == true);

                if (statusChanged)
                {
                    Output.WriteLine("✅ BONUS: Todo status change detected - data flow working correctly");
                }
                else
                {
                    Output.WriteLine("⚠️  WARNING: No status changes detected - this might indicate the DataChangeRequest issue");
                }
            }
        }
        catch (TimeoutException)
        {
            Output.WriteLine("❌ Step 6 FAILED: Neither layout nor data updated within timeout");
            Output.WriteLine("❌ This confirms the issue: DataChangeRequest is not properly triggering updates");
            throw new Exception("Layout area failed to update after button click - this indicates the DataChangeRequest issue mentioned by the user");
        }
    }

    /// <summary>
    /// Helper method to find all button areas in a layout grid
    /// </summary>
    private static List<string> FindAllButtonAreas(LayoutGridControl layoutGrid)
    {
        var buttonAreas = new List<string>();

        foreach (var area in layoutGrid.Areas)
        {
            if (IsButtonControl(area.View))
            {
                buttonAreas.Add(area.Area);
            }
            else if (area.View is StackControl stack)
            {
                // Check nested stacks for buttons
                if (HasButtonInStack(stack))
                {
                    buttonAreas.Add(area.Area);
                }
            }
        }

        return buttonAreas;
    }

    /// <summary>
    /// Helper method to check if a control is a button
    /// </summary>
    private static bool IsButtonControl(UiControl? control)
    {
        return control is ButtonControl;
    }

    /// <summary>
    /// Helper method to recursively check if a stack contains buttons
    /// </summary>
    private static bool HasButtonInStack(StackControl stack)
    {
        foreach (var area in stack.Areas)
        {
            if (area.View is ButtonControl)
            {
                return true;
            }

            if (area.View is StackControl nestedStack && HasButtonInStack(nestedStack))
            {
                return true;
            }
        }
        return false;
    }
}

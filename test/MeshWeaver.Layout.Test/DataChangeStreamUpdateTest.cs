using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Test entity to emulate the todo list update situation
/// </summary>
public record TestTaskItem(
    [property: Key] string Id,
    string Title,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt
)
{
    /// <summary>
    /// Initial test data for seeding
    /// </summary>
    public static readonly TestTaskItem[] InitialData = 
    [
        new("task-1", "First Task", "Pending", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(-1)),
        new("task-2", "Second Task", "InProgress", DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-2)),
        new("task-3", "Third Task", "Completed", DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-30))
    ];
}

/// <summary>
/// Test to emulate the todo list update situation where DataChangeRequest should trigger layout area updates
/// This test follows the exact pattern described:
/// 1. ConfigureHost with some entity type (TestTaskItem) with initial data
/// 2. Create layout area that subscribes to host.Workspace.GetStream<TestTaskItem>() and shows a property
/// 3. From client emit DataChangeRequest to change a property
/// 4. Verify that view updates to reflect the change
/// </summary>
public class DataChangeStreamUpdateTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string TaskListArea = nameof(TaskListArea);
    private const string TaskCountArea = nameof(TaskCountArea);

    /// <summary>
    /// Step 1: Configure host with TestTaskItem entity type and initial data
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r =>
                r.RouteAddress<ClientAddress>((_, d) => d.Package(r.Hub.JsonSerializerOptions))
            )
            .AddData(data =>
                data.AddSource(ds =>
                    ds.WithType<TestTaskItem>(t =>
                        t.WithInitialData(TestTaskItem.InitialData)
                    )
                )
            )
            .AddLayout(layout =>
                layout
                    // Step 2: Create layout area that subscribes to stream and shows property we'll change
                    .WithView(TaskListArea, TaskListView)
                    .WithView(TaskCountArea, TaskCountView)
            );
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) 
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// Layout area that subscribes to TestTaskItem stream and displays items with their status
    /// Shows property that we're going to change (Status)
    /// </summary>
    private static IObservable<UiControl> TaskListView(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface

        return host.Workspace
            .GetStream<TestTaskItem>()
            .Select(taskItems => CreateTaskListMarkdown(taskItems))
            .StartWith(Controls.Markdown("# Task List\n\n*Loading tasks...*"));
    }

    /// <summary>
    /// Layout area that shows count of tasks by status - useful for testing updates
    /// </summary>
    private static IObservable<UiControl> TaskCountView(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface

        return host.Workspace
            .GetStream<TestTaskItem>()
            .Select(taskItems => CreateTaskCountMarkdown(taskItems))
            .StartWith(Controls.Markdown("# Task Count\n\n*Loading task statistics...*"));
    }

    /// <summary>
    /// Creates markdown display of task list showing status property
    /// </summary>
    private static UiControl CreateTaskListMarkdown(IReadOnlyCollection<TestTaskItem> taskItems)
    {
        var markdown = "# Task List\n\n";
        
        if (!taskItems.Any())
        {
            markdown += "*No tasks found.*";
            return Controls.Markdown(markdown);
        }

        foreach (var task in taskItems.OrderBy(t => t.CreatedAt))
        {
            var statusIcon = task.Status switch
            {
                "Pending" => "⏳",
                "InProgress" => "🔄", 
                "Completed" => "✅",
                _ => "❓"
            };

            markdown += $"## {statusIcon} {task.Title}\n";
            markdown += $"**Status:** {task.Status}\n";
            markdown += $"**Created:** {task.CreatedAt:yyyy-MM-dd HH:mm}\n";
            markdown += $"**Updated:** {task.UpdatedAt:yyyy-MM-dd HH:mm}\n\n";
        }

        return Controls.Markdown(markdown);
    }

    /// <summary>
    /// Creates markdown showing count of tasks by status
    /// </summary>
    private static UiControl CreateTaskCountMarkdown(IReadOnlyCollection<TestTaskItem> taskItems)
    {
        var markdown = "# Task Count\n\n";
        
        if (!taskItems.Any())
        {
            markdown += "*No tasks found.*";
            return Controls.Markdown(markdown);
        }

        var statusCounts = taskItems
            .GroupBy(t => t.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        markdown += $"**Total Tasks:** {taskItems.Count}\n\n";
        markdown += "## By Status\n";
        
        foreach (var status in new[] { "Pending", "InProgress", "Completed" })
        {
            var count = statusCounts.GetValueOrDefault(status, 0);
            var icon = status switch
            {
                "Pending" => "⏳",
                "InProgress" => "🔄",
                "Completed" => "✅",
                _ => "❓"
            };
            markdown += $"- {icon} **{status}:** {count}\n";
        }

        return Controls.Markdown(markdown);
    }

    /// <summary>
    /// Test that verifies the complete data change and view update flow
    /// </summary>
    [HubFact]
    public async Task DataChangeRequest_ShouldUpdateLayoutAreaViews()
    {
        // Get client and workspace
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Step 2: Subscribe to layout area (simulating layout area rendering)
        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(TaskListArea)
        );

        // Verify initial data is loaded in layout area
        var initialControl = await stream
            .GetControlStream(TaskListArea)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null && x.ToString().Contains("First Task"));

        initialControl.Should().NotBeNull();
        var initialContent = initialControl.ToString();
        initialContent.Should().Contain("First Task");
        initialContent.Should().Contain("Status:** Pending"); // Initial status

        Output.WriteLine($"✅ Initial layout area loaded with content: {initialContent.Length} chars");

        // Step 3: Get the task we want to update
        var tasksData = await workspace
            .GetRemoteStream<TestTaskItem>(new HostAddress())
            .Timeout(5.Seconds())
            .FirstAsync();

        var taskToUpdate = tasksData.First(t => t.Id == "task-1");
        taskToUpdate.Should().NotBeNull();
        taskToUpdate.Status.Should().Be("Pending");

        Output.WriteLine($"🎯 Target task found: '{taskToUpdate.Title}' with status '{taskToUpdate.Status}'");

        // Step 4: Emit DataChangeRequest to change the status
        var updatedTask = taskToUpdate with 
        { 
            Status = "InProgress", 
            UpdatedAt = DateTime.UtcNow 
        };

        var changeRequest = new DataChangeRequest().WithUpdates(updatedTask);
        
        Output.WriteLine($"📤 Sending DataChangeRequest to change status: {taskToUpdate.Status} → {updatedTask.Status}");

        var updatedControlTask = stream
            .GetControlStream(TaskListArea)
            .Skip(1)
            .Where(x => x != null && x.ToString().Contains("Status:** InProgress"))
            .Timeout(10.Seconds())
            .FirstAsync();
        client.Post(changeRequest, o => o.WithTarget(new HostAddress()));

        // Step 5: Verify that layout area updates to show the change
        var updatedControl = await updatedControlTask;

        updatedControl.Should().NotBeNull();
        var updatedContent = updatedControl.ToString();
        updatedContent.Should().Contain("First Task");
        updatedContent.Should().Contain("Status:** InProgress"); // Updated status
        updatedContent.Should().NotContain("Status:** Pending"); // Old status should not be there for this task

        Output.WriteLine($"✅ Layout area updated successfully with new content: {updatedContent.Length} chars");

        // Additional verification: Check that task count view also updates
        var countStream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(TaskCountArea)
        );

        var updatedCountControl = await countStream
            .GetControlStream(TaskCountArea)
            .Where(x => x != null && x.ToString().Contains("🔄 **InProgress:** 2")) // Should now have 2 InProgress tasks
            .Timeout(10.Seconds())
            .FirstAsync();

        updatedCountControl.Should().NotBeNull();
        var countContent = updatedCountControl.ToString();
        countContent.Should().Contain("🔄 **InProgress:** 2"); // task-2 was already InProgress, now task-1 too
        countContent.Should().Contain("⏳ **Pending:** 0"); // No more pending tasks

        Output.WriteLine($"✅ Task count view also updated correctly: {countContent}");
        Output.WriteLine("🎉 Test completed successfully - DataChangeRequest properly updates layout area views!");
    }

    /// <summary>
    /// Test to verify multiple simultaneous updates work correctly
    /// </summary>
    [HubFact]
    public async Task MultipleDataChanges_ShouldUpdateLayoutAreaViews()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(TaskCountArea)
        );

        // Wait for initial data
        await stream
            .GetControlStream(TaskCountArea)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null && x.ToString().Contains("Total Tasks"));

        // Get initial tasks
        var tasksData = await workspace
            .GetRemoteStream<TestTaskItem>(new HostAddress())
            .Timeout(5.Seconds())
            .FirstAsync();

        // Update multiple tasks simultaneously
        var updatedTasks = tasksData.Select(task => task with 
        { 
            Status = "Completed", 
            UpdatedAt = DateTime.UtcNow 
        }).ToArray();

        var changeRequest = new DataChangeRequest().WithUpdates(updatedTasks);
        
        Output.WriteLine($"📤 Sending DataChangeRequest to complete all {updatedTasks.Length} tasks");
        client.Post(changeRequest, o => o.WithTarget(new HostAddress()));

        // Verify all tasks are now completed
        var allCompletedControl = await stream
            .GetControlStream(TaskCountArea)
            .Where(x => x != null && x.ToString().Contains("✅ **Completed:** 3"))
            .Timeout(10.Seconds())
            .FirstAsync();

        allCompletedControl.Should().NotBeNull();
        var content = allCompletedControl.ToString();
        content.Should().Contain("✅ **Completed:** 3");
        content.Should().Contain("⏳ **Pending:** 0");
        content.Should().Contain("🔄 **InProgress:** 0");

        Output.WriteLine("✅ Multiple data changes processed correctly");
    }

    /// <summary>
    /// Test to verify that creating new items works
    /// </summary>
    [HubFact]
    public async Task CreateNewTask_ShouldUpdateLayoutAreaViews()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(TaskCountArea)
        );

        // Wait for initial data (should show 3 tasks)
        await stream
            .GetControlStream(TaskCountArea)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null && x.ToString().Contains("Total Tasks:** 3"));

        // Create a new task
        var newTask = new TestTaskItem(
            "task-4",
            "New Task from Test",
            "Pending",
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        var createRequest = new DataChangeRequest().WithCreations(newTask);
        
        Output.WriteLine($"📤 Creating new task: '{newTask.Title}'");
        client.Post(createRequest, o => o.WithTarget(new HostAddress()));

        // Verify task count increased
        var updatedControl = await stream
            .GetControlStream(TaskCountArea)
            .Where(x => x != null && x.ToString().Contains("Total Tasks:** 4"))
            .Timeout(10.Seconds())
            .FirstAsync();

        updatedControl.Should().NotBeNull();
        var content = updatedControl.ToString();
        content.Should().Contain("Total Tasks:** 4");
        
        Output.WriteLine("✅ New task creation updated layout area correctly");
    }

    /// <summary>
    /// Test to verify that deleting items works
    /// </summary>
    [HubFact]
    public async Task DeleteTask_ShouldUpdateLayoutAreaViews()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var stream = workspace.GetRemoteStream<System.Text.Json.JsonElement, LayoutAreaReference>(
            new HostAddress(),
            new LayoutAreaReference(TaskListArea)
        );

        // Wait for initial data
        await stream
            .GetControlStream(TaskListArea)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null && x.ToString().Contains("First Task"));

        // Get task to delete
        var tasksData = await workspace
            .GetRemoteStream<TestTaskItem>(new HostAddress())
            .Timeout(5.Seconds())
            .FirstAsync();

        var taskToDelete = tasksData.First(t => t.Id == "task-1");
        var deleteRequest = new DataChangeRequest().WithDeletions(taskToDelete);
        
        Output.WriteLine($"📤 Deleting task: '{taskToDelete.Title}'");
        client.Post(deleteRequest, o => o.WithTarget(new HostAddress()));

        // Verify task is no longer in the list
        var updatedControl = await stream
            .GetControlStream(TaskListArea)
            .Where(x => x != null && !x.ToString().Contains("First Task"))
            .Timeout(10.Seconds())
            .FirstAsync();

        updatedControl.Should().NotBeNull();
        var content = updatedControl.ToString();
        content.Should().NotContain("First Task");
        content.Should().Contain("Second Task"); // Other tasks should still be there
        
        Output.WriteLine("✅ Task deletion updated layout area correctly");
    }
}

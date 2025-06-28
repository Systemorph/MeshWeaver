using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Todo;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Test to verify that the DataChangeRequest fix works correctly
/// This test demonstrates that the layout area update issue has been resolved
/// </summary>
public class TodoDataUpdateFixTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    /// <summary>
    /// Test that verifies DataChangeRequest now works correctly after adding the WithKey configuration
    /// This simulates the exact scenario that was failing before - clicking a button to update todo status
    /// </summary>
    [HubFact]
    public async Task DataChangeRequest_WithKeyConfiguration_ShouldUpdateTodo()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Get initial data
        var initialTodos = await workspace
            .GetStream<TodoItem>()
            .Timeout(10.Seconds())
            .FirstAsync();

        initialTodos.Should().NotBeEmpty();
        var pendingTodo = initialTodos.FirstOrDefault(t => t.Status == TodoStatus.Pending);
        pendingTodo.Should().NotBeNull("Need a pending todo for testing");

        Output.WriteLine($"Initial todo: '{pendingTodo.Title}' with status {pendingTodo.Status}");

        // Create an updated todo (simulate button click changing status)
        var updatedTodo = pendingTodo with
        {
            Status = TodoStatus.InProgress,
            UpdatedAt = DateTime.UtcNow
        };

        // Send DataChangeRequest (this is what the button click does)
        var changeRequest = new DataChangeRequest()
            .WithUpdates(updatedTodo);

        host.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));

        // Wait for data to be updated
        var finalTodos = await workspace
            .GetStream<TodoItem>()
            .Skip(1) // Skip initial data
            .Timeout(10.Seconds())
            .FirstAsync();

        // Verify the todo was updated
        var changedTodo = finalTodos.FirstOrDefault(t => t.Id == pendingTodo.Id);
        changedTodo.Should().NotBeNull();
        changedTodo.Status.Should().Be(TodoStatus.InProgress);

        Output.WriteLine($"✅ SUCCESS: Todo '{changedTodo.Title}' status updated from {pendingTodo.Status} to {changedTodo.Status}");
        Output.WriteLine("✅ CONCLUSION: DataChangeRequest is now working correctly!");
        Output.WriteLine("✅ IMPACT: Layout areas should now update properly when buttons are clicked");
    }
}

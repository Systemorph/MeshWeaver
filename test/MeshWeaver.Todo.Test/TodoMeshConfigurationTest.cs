using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Todo;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Simple test to verify Todo mesh configuration and data source functionality
/// </summary>
public class TodoMeshConfigurationTest(ITestOutputHelper output) : TodoDataTestBase(output)
{

    /// <summary>
    /// Test that verifies Todo mesh node is properly configured
    /// </summary>
    [Fact]
    public async Task Mesh_Should_Have_Todo_Application_Configured()
    {
        // Arrange & Act
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get the Todo data to verify Todo data source is configured
        var todoItems = await workspace
            .GetObservable<TodoItem>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        // Assert
        todoItems.Should().NotBeNull();
        todoItems.Should().NotBeEmpty("Todo data source should be configured with sample data");

        var firstTodo = todoItems.FirstOrDefault();
        firstTodo.Should().NotBeNull("at least one todo item should exist");
        firstTodo!.Id.Should().NotBeEmpty("todo item should have an ID");
        firstTodo.Title.Should().NotBeNullOrEmpty("todo item should have a title");

        Output.WriteLine($"✅ Mesh configuration test PASSED: Found {todoItems.Count} todo items");
        Output.WriteLine($"Sample todo: {firstTodo.Title} (Status: {firstTodo.Status})");
    }

    /// <summary>
    /// Test that verifies data change requests work properly with the key configuration
    /// </summary>
    [Fact]
    public async Task DataSource_Should_Support_DataChangeRequest_With_Key_Configuration()
    {
        // Arrange
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var originalTodos = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        var targetTodo = originalTodos?.FirstOrDefault(t => t.Status == TodoStatus.Pending);
        targetTodo.Should().NotBeNull("Expected to find at least one pending todo for testing");

        var originalStatus = targetTodo!.Status;
        var newStatus = originalStatus == TodoStatus.Pending ? TodoStatus.InProgress : TodoStatus.Pending;

        // Act - Create and send a data change request
        var updatedTodo = targetTodo with { Status = newStatus };
        var changeRequest = DataChangeRequest.Update(new object[] { updatedTodo });

        client.Post(changeRequest, options => options.WithTarget(TodoApplicationAttribute.Address));

        // Give the system a moment to process the change
        await Task.Delay(1000);

        // Get updated data
        var updatedTodos = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        var updatedTargetTodo = updatedTodos?.FirstOrDefault(t => t.Id == targetTodo.Id);

        // Assert
        updatedTargetTodo.Should().NotBeNull("todo item should still exist after update");
        updatedTargetTodo!.Status.Should().Be(newStatus,
            $"status should be updated from {originalStatus} to {newStatus}");
        updatedTargetTodo.Title.Should().Be(targetTodo.Title,
            "other properties should remain unchanged");

        Output.WriteLine($"✅ DataChangeRequest test PASSED: Updated todo '{targetTodo.Title}' from {originalStatus} to {newStatus}");
    }
}

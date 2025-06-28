using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Todo;
using Xunit.Abstractions;

namespace MeshWeaver.Todo.Test;

/// <summary>
/// Simple test to verify basic Todo functionality
/// </summary>
public class SimpleTodoTest(ITestOutputHelper output) : HubTestBase(output)
{
    [HubFact]
    public void CanCreateTodoItem()
    {
        var todo = new TodoItem
        {
            Title = "Test Todo",
            Status = TodoStatus.Pending
        };

        todo.Should().NotBeNull();
        todo.Title.Should().Be("Test Todo");
        todo.Status.Should().Be(TodoStatus.Pending);
    }
}

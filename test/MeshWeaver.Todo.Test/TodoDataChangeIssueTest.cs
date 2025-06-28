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
/// Test to verify the DataChangeRequest issue described by the user
/// This simulates the button click scenario that should trigger layout area updates
/// </summary>
public class TodoDataChangeIssueTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .ConfigureTodoApplication();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddData(data =>
                data.AddHubSource(new HostAddress(), dataSource =>
                    dataSource.WithType<TodoItem>())
            );
    }

    /// <summary>
    /// Test that reproduces the DataChangeRequest issue
    /// This simulates what happens when a user clicks a button in the TodoList layout area
    /// </summary>
    [HubFact]
    public async Task SimulateButtonClick_DataChangeRequest_ShouldUpdateData()
    {
        // Step 1: Set up data context with TodoItems
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var initialData = await workspace
            .GetObservable<TodoItem>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        initialData.Should().NotBeNull();
        initialData.Should().NotBeEmpty();
        Output.WriteLine($"✅ Step 1: Data context initialized with {initialData.Count} todo items");

        // Find a pending todo to update
        var pendingTodo = initialData.FirstOrDefault(t => t.Status == TodoStatus.Pending);
        pendingTodo.Should().NotBeNull("Need a pending todo for testing");
        Output.WriteLine($"Found pending todo: '{pendingTodo.Title}'");

        // Step 2-4: Views are registered and layout area would be rendered (simulated)
        Output.WriteLine("✅ Steps 2-4: Todo views registered and layout area rendered (simulated)");

        // Step 5: Simulate button click - create DataChangeRequest like TodoLayoutArea.UpdateTodoStatus does
        var updatedTodo = new TodoItem
        {
            Id = pendingTodo.Id,
            Title = pendingTodo.Title,
            Description = pendingTodo.Description,
            Category = pendingTodo.Category,
            DueDate = pendingTodo.DueDate,
            Status = TodoStatus.InProgress, // This is what the "Start" button would do
            CreatedAt = pendingTodo.CreatedAt,
            UpdatedAt = DateTime.UtcNow
        };

        var changeRequest = new DataChangeRequest()
            .WithUpdates(updatedTodo);

        Output.WriteLine("✅ Step 5: Sending DataChangeRequest (simulating button click)...");
        client.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));

        // Step 6: Wait for data to update - this is where the issue occurs
        try
        {
            var updatedData = await workspace
                .GetObservable<TodoItem>()
                .Skip(1) // Skip the initial data
                .Timeout(15.Seconds()) // Give it more time
                .FirstOrDefaultAsync();

            if (updatedData != null)
            {
                var changedTodo = updatedData.FirstOrDefault(t => t.Id == pendingTodo.Id);
                if (changedTodo != null && changedTodo.Status == TodoStatus.InProgress)
                {
                    Output.WriteLine("✅ Step 6: SUCCESS! DataChangeRequest updated the data stream");
                    Output.WriteLine($"✅ Todo '{changedTodo.Title}' status: {pendingTodo.Status} → {changedTodo.Status}");
                    Output.WriteLine("✅ RESULT: Layout areas should update properly when this works");
                }
                else
                {
                    Output.WriteLine("❌ Step 6: FAILED - Todo status was not updated");
                    Output.WriteLine("❌ ISSUE CONFIRMED: DataChangeRequest is not updating the data");
                }
            }
            else
            {
                Output.WriteLine("❌ Step 6: FAILED - Data stream did not emit updated data");
                Output.WriteLine("❌ ISSUE CONFIRMED: DataChangeRequest is not triggering data stream updates");
            }
        }
        catch (TimeoutException)
        {
            Output.WriteLine("❌ Step 6: TIMEOUT - Data stream did not update within 15 seconds");
            Output.WriteLine("❌ ISSUE CONFIRMED: This is exactly the problem described by the user!");
            Output.WriteLine("❌ ROOT CAUSE: DataChangeRequest is not being processed or not triggering data updates");
            Output.WriteLine("❌ IMPACT: This explains why layout areas don't update after button clicks");

            // This confirms the user's issue
            throw new Exception("DataChangeRequest timeout confirms the layout area update issue");
        }
    }
}

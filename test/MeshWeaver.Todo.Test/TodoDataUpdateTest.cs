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
/// Test that focuses on the DataChangeRequest update issue
/// This test verifies the core data flow problem that prevents layout areas from updating
/// </summary>
public class TodoDataUpdateTest(ITestOutputHelper output) : HubTestBase(output)
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
    /// Step 1: Verify data context initializes with TodoItems
    /// </summary>
    [HubFact]
    public async Task Step1_DataContext_ShouldInitializeWithTodoItems()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        var todoData = await workspace
            .GetObservable<TodoItem>()
            .Timeout(10.Seconds())
            .FirstOrDefaultAsync();

        todoData.Should().NotBeNull();
        todoData.Should().NotBeEmpty();

        Output.WriteLine($"✅ Step 1 PASSED: Initialized with {todoData.Count} todo items");

        var sampleTodos = TodoSampleData.GetSampleTodos().ToList();
        todoData.Should().HaveCount(sampleTodos.Count);
    }

    /// <summary>
    /// Step 2: Test DataChangeRequest directly - this is where the issue likely occurs
    /// This simulates what happens when a button is clicked in the layout area
    /// </summary>
    [HubFact]
    public async Task Step2_DataChangeRequest_ShouldUpdateTodoStatus()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();

        // Get initial data
        var initialTodos = await workspace
            .GetObservable<TodoItem>()
            .Timeout(5.Seconds())
            .FirstOrDefaultAsync();

        var pendingTodo = initialTodos.FirstOrDefault(t => t.Status == TodoStatus.Pending);
        pendingTodo.Should().NotBeNull("Need a pending todo for testing");

        Output.WriteLine($"Found pending todo: {pendingTodo.Title}");

        // Create updated todo (simulate what UpdateTodoStatus does)
        var updatedTodo = pendingTodo with
        {
            Status = TodoStatus.InProgress
        };

        // Submit DataChangeRequest (simulate what SubmitTodoUpdate does)
        var changeRequest = new DataChangeRequest()
            .WithUpdates(updatedTodo);

        Output.WriteLine("Sending DataChangeRequest...");
        client.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));

        // Wait for data stream to update
        try
        {
            var updatedTodos = await workspace
                .GetObservable<TodoItem>()
                .Skip(1) // Skip initial data
                .Timeout(10.Seconds())
                .FirstOrDefaultAsync();

            if (updatedTodos != null)
            {
                var changedTodo = updatedTodos.FirstOrDefault(t => t.Id == pendingTodo.Id);
                if (changedTodo != null && changedTodo.Status == TodoStatus.InProgress)
                {
                    Output.WriteLine("✅ Step 2 PASSED: DataChangeRequest successfully updated todo status");
                    Output.WriteLine($"Todo '{changedTodo.Title}' changed from {pendingTodo.Status} to {changedTodo.Status}");
                }
                else
                {
                    Output.WriteLine("❌ Step 2 FAILED: Todo status was not updated");
                    throw new Exception("DataChangeRequest did not update the todo status - this confirms the issue");
                }
            }
            else
            {
                Output.WriteLine("❌ Step 2 FAILED: No data update received");
                throw new Exception("Workspace data stream did not emit updated data - this confirms the issue");
            }
        }
        catch (TimeoutException)
        {
            Output.WriteLine("❌ Step 2 FAILED: DataChangeRequest update timed out");
            Output.WriteLine("❌ This confirms the issue: DataChangeRequest is not properly updating the workspace data stream");
            throw new Exception("DataChangeRequest failed to trigger data stream update - this is the root cause of the layout area not updating");
        }
    }

    /// <summary>
    /// Additional test to verify the data handler is working
    /// </summary>
    [HubFact]
    public async Task DataHandler_ShouldBeConfigured()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();

        // Verify we can get the data stream
        var stream = workspace.GetStream<TodoItem>();
        stream.Should().NotBeNull();

        var data = await stream.Timeout(5.Seconds()).FirstOrDefaultAsync();
        data.Should().NotBeNull();

        Output.WriteLine($"✅ Data handler is configured and working with {data.Count} items");
    }
}

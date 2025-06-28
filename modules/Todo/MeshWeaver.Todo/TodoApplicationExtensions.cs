using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Todo;

/// <summary>
/// Extensions for configuring the Todo application
/// </summary>
public static class TodoApplicationExtensions
{
    /// <summary>
    /// Configures the Todo application hub
    /// </summary>
    /// <param name="configuration">The message hub configuration</param>
    /// <returns>The configured message hub configuration</returns>
    public static MessageHubConfiguration ConfigureTodoApplication(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithTypes(
                typeof(TodoStatus)
            )
            .AddData(data =>
                data.AddSource(dataSource =>
                    dataSource.WithType<TodoItem>(t =>
                        t.WithKey(todo => todo.Id)
                         .WithInitialData(TodoSampleData.GetSampleTodos())
                    )
                )
            )
            .AddLayout(layout =>
                layout.WithView(nameof(TodoLayoutArea.TodoList), TodoLayoutArea.TodoList)
                      .WithView(nameof(TodoLayoutArea.TodosByCategory), TodoLayoutArea.TodosByCategory)
                      .WithView(nameof(TodoLayoutArea.TodoSummary), TodoLayoutArea.TodoSummary)
                      .WithView("TodoDetails", (_, _) => TodoViews.CreateTodoDetailsView())
                      .WithView(nameof(TodoManagement.AddTodo), TodoManagement.AddTodo)
                      .WithView(nameof(TodoManagement.TodoListWithActions), TodoManagement.TodoListWithActions)
                      .WithView(nameof(TodoManagement.TodoDemo), TodoManagement.TodoDemo)
            );
    }
}

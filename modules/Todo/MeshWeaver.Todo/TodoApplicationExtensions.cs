using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaver.Todo.Domain;
using MeshWeaver.Todo.LayoutAreas;
using MeshWeaver.Todo.SampleData;

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
                .AddSource(dataSource =>
                    dataSource.WithType<TodoCategory>(t =>
                        t.WithKey(category => category.Name)
                         .WithInitialData(TodoSampleData.GetSampleCategories())
                    )
                )
            )
            .AddLayout(layout =>
                layout.WithView(nameof(TodoLayoutArea.TodoList), TodoLayoutArea.TodoList)
                      .WithView(nameof(TodoLayoutArea.TodosByCategory), TodoLayoutArea.TodosByCategory)
                      .WithView(nameof(TodoLayoutArea.TodoSummary), TodoLayoutArea.TodoSummary)
                      .WithView(nameof(TodoLayoutArea.PlanningView), TodoLayoutArea.PlanningView)
                      .WithView(nameof(TodoLayoutArea.MyActiveTasks), TodoLayoutArea.MyActiveTasks)
                      .WithView(nameof(TodoLayoutArea.UnassignedTasks), TodoLayoutArea.UnassignedTasks)
                      .WithView(nameof(TodoLayoutArea.TodaysFocus), TodoLayoutArea.TodaysFocus)
            );
    }
}

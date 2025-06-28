using MeshWeaver.Layout;

namespace MeshWeaver.Todo;

/// <summary>
/// Views for the Todo application
/// </summary>
public static class TodoViews
{
    /// <summary>
    /// Creates the Todo list view
    /// </summary>
    /// <returns>A UI control for displaying the todo list</returns>
    public static UiControl CreateTodoListView()
    {
        return Controls.Html("""
            <div style="padding: 20px;">
                <h2>Todo List</h2>
                <p>Welcome to the Todo application! Here you can manage your todo items.</p>
                <div style="margin-top: 20px;">
                    <h3>Features:</h3>
                    <ul>
                        <li>Create new todo items</li>
                        <li>Organize by categories</li>
                        <li>Set due dates</li>
                        <li>Track status (Pending, In Progress, Completed, Cancelled)</li>
                    </ul>
                </div>
            </div>
            """);
    }

    /// <summary>
    /// Creates the Todo details view
    /// </summary>
    /// <returns>A UI control for displaying todo details</returns>
    public static UiControl CreateTodoDetailsView()
    {
        return Controls.Html("""
            <div style="padding: 20px;">
                <h2>Todo Details</h2>
                <p>Todo item details will be displayed here.</p>
            </div>
            """);
    }
}

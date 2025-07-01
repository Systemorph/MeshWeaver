using System.Reactive.Linq;
using System.Text;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Todo.Domain;

namespace MeshWeaver.Todo.LayoutAreas;

/// <summary>
/// Interactive Todo management layout areas with add/edit functionality
/// </summary>
public static class TodoManagement
{
    /// <summary>
    /// Layout area for adding new todos
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>Observable UI control stream</returns>
    public static IObservable<UiControl> AddTodo(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface

        var newTodo = new TodoItem
        {
            Title = "",
            Description = "",
            Category = "General",
            Status = TodoStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        return Observable.Return(
            Controls.Stack
                .WithView(Controls.H3("Add New Todo"))
                .WithView(host.Edit(newTodo, todo =>
                    Controls.Stack
                        .WithView((hostInner, ctxInner) =>
                            Controls.Button("Add Todo")
                                .WithClickAction(actionContext =>
                                {
                                    // Validate required fields
                                    if (string.IsNullOrWhiteSpace(todo.Title))
                                        return Task.CompletedTask;

                                    // Create the todo with current timestamp
                                    var todoToAdd = todo with
                                    {
                                        CreatedAt = DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow
                                    };

                                    // Submit data change request
                                    var changeRequest = new DataChangeRequest()
                                        .WithCreations(todoToAdd);

                                    actionContext.Host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
                                    return Task.CompletedTask;
                                })
                        )
                        .WithView(host.Workspace
                            .GetStream<TodoItem>()
                            .Select(todos => CreateTodoAddedConfirmation(todos.Count())))
                ))
        );
    }

    /// <summary>
    /// Layout area for Todo list with interactive controls
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>Observable UI control stream</returns>
    public static IObservable<UiControl> TodoListWithActions(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface

        return host.Workspace.GetStream<TodoItem>()
            .Select(items => CreateInteractiveTodoListMarkdown(items, host));
    }

    /// <summary>
    /// Creates a markdown representation with interactive buttons for todo actions using a layout grid
    /// </summary>
    /// <param name="todoItems">The collection of todo items</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A Stack control with markdown and action buttons</returns>
    private static UiControl CreateInteractiveTodoListMarkdown(IReadOnlyCollection<TodoItem> todoItems, LayoutAreaHost host)
    {
        if (!todoItems.Any())
        {
            return Controls.Stack
                .WithView(Controls.H2("📝 Todo List with Actions"))
                .WithView(Controls.Markdown("*No todo items found. Add your first todo to get started!*"));
        }

        // Order by due date (nulls last), then by created date
        var orderedTodos = todoItems
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.CreatedAt)
            .ToList();

        // Start with LayoutGrid as the main container
        var mainGrid = Controls.LayoutGrid
            .WithSkin(skin => skin.WithSpacing(8));

        // First row: Title and Add New Todo button
        mainGrid = mainGrid
            .WithView(Controls.H2("📝 Todo List with Actions"),
                     skin => skin.WithXs(12).WithSm(8).WithMd(9))
            .WithView(Controls.Button("➕ Add New Todo")
                .WithClickAction(_ => Task.CompletedTask),
                     skin => skin.WithXs(12).WithSm(4).WithMd(3));

        // Group by status for better organization
        var statusGroups = orderedTodos.GroupBy(t => t.Status).ToList();

        foreach (var statusGroup in statusGroups.OrderBy(g => (int)g.Key))
        {
            var statusIcon = GetStatusIcon(statusGroup.Key);
            var statusName = statusGroup.Key.ToString();

            // Heading row: Status header and action button (or placeholder)
            UiControl statusActionButton = statusGroup.Key switch
            {
                TodoStatus.Completed => Controls.Button("Archive All")
                    .WithClickAction(_ => Task.CompletedTask),
                TodoStatus.Pending => Controls.Button("Start All")
                    .WithClickAction(_ => Task.CompletedTask),
                _ => Controls.Html("") // Empty placeholder for other statuses
            };

            mainGrid = mainGrid
                .WithView(Controls.H3($"{statusIcon} {statusName} ({statusGroup.Count()})"),
                         skin => skin.WithXs(12).WithSm(8).WithMd(9))
                .WithView(statusActionButton,
                         skin => skin.WithXs(12).WithSm(4).WithMd(3));

            // Add each todo using the Northwind pattern: content + buttons as separate views
            foreach (var todo in statusGroup)
            {
                var todoMarkdown = CreateTodoItemMarkdown(todo);
                var buttonStack = CreateActionButtons(todo, host);

                // Content view - takes most of the space
                mainGrid = mainGrid
                    .WithView(Controls.Markdown(todoMarkdown),
                             skin => skin.WithXs(12).WithSm(8).WithMd(9));

                // Actions view - takes remaining space  
                mainGrid = mainGrid
                    .WithView(buttonStack,
                             skin => skin.WithXs(12).WithSm(4).WithMd(3));
            }
        }

        return mainGrid;
    }

    /// <summary>
    /// Submits an update request for a todo item
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="updatedTodo">The updated todo item</param>
    private static void SubmitTodoUpdate(LayoutAreaHost host, TodoItem updatedTodo)
    {
        var changeRequest = new DataChangeRequest()
            .WithUpdates(updatedTodo);

        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
    }

    /// <summary>
    /// Submits a delete request for a todo item
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="todoToDelete">The todo item to delete</param>
    private static void SubmitTodoDelete(LayoutAreaHost host, TodoItem todoToDelete)
    {
        var changeRequest = new DataChangeRequest()
            .WithDeletions(todoToDelete);

        host.Hub.Post(changeRequest, o => o.WithTarget(TodoApplicationAttribute.Address));
    }

    /// <summary>
    /// Creates markdown for a single todo item
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <returns>Formatted markdown string</returns>
    private static string CreateTodoItemMarkdown(TodoItem todo)
    {
        var sb = new StringBuilder();

        // Status and title
        var statusIcon = GetStatusIcon(todo.Status);
        sb.Append($"**{todo.Title}**");

        // Category badge
        if (todo.Category != "General")
        {
            sb.Append($" `{todo.Category}`");
        }

        // Due date
        if (todo.DueDate.HasValue)
        {
            var dueDate = todo.DueDate.Value;
            var isOverdue = dueDate < DateTime.UtcNow && todo.Status != TodoStatus.Completed;
            var isDueToday = dueDate.Date == DateTime.UtcNow.Date;

            if (isOverdue)
            {
                sb.Append($" ⚠️ **OVERDUE** ({dueDate:MMM d, yyyy})");
            }
            else if (isDueToday)
            {
                sb.Append($" 📅 **DUE TODAY** ({dueDate:MMM d, yyyy})");
            }
            else
            {
                sb.Append($" ({dueDate:MMM d, yyyy})");
            }
        }

        // Description
        if (!string.IsNullOrEmpty(todo.Description))
        {
            sb.AppendLine();
            sb.Append($"  *{todo.Description}*");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Creates a confirmation message when a todo is added
    /// </summary>
    /// <param name="totalCount">Total number of todos</param>
    /// <returns>A confirmation control</returns>
    private static UiControl CreateTodoAddedConfirmation(int totalCount)
    {
        return Controls.Markdown($"✅ **Total todos: {totalCount}**");
    }

    /// <summary>
    /// Gets the appropriate status icon for a todo status
    /// </summary>
    /// <param name="status">The todo status</param>
    /// <returns>Status icon string</returns>
    private static string GetStatusIcon(TodoStatus status)
    {
        return status switch
        {
            TodoStatus.Pending => "⏳",
            TodoStatus.InProgress => "🔄",
            TodoStatus.Completed => "✅",
            TodoStatus.Cancelled => "❌",
            _ => "❓"
        };
    }

    /// <summary>
    /// Layout area for demonstrating all Todo features
    /// </summary>
    /// <param name="host">The layout area host</param>
    /// <param name="context">The rendering context</param>
    /// <returns>Observable UI control stream</returns>
    public static IObservable<UiControl> TodoDemo(LayoutAreaHost host, RenderingContext context)
    {
        _ = context; // Unused parameter but required by interface
        _ = host; // Unused parameter but required by interface

        return Observable.Return(
            Controls.Stack
                .WithView(Controls.H2("🚀 Todo Application Demo"))
                .WithView(Controls.Markdown(@"
Welcome to the MeshWeaver Todo Application! This demonstrates a complete data management solution with reactive UI.

## Available Views

### 📋 Read-Only Views
- **[Todo List](/app/Todo/TodoList)** - Main todo list with status grouping
- **[Todos by Category](/app/Todo/TodosByCategory)** - Category-based organization
- **[Todo Summary](/app/Todo/TodoSummary)** - Statistics and progress overview

### ⚡ Interactive Views  
- **[Add Todo](/app/Todo/AddTodo)** - Create new todo items with forms
- **[Todo Management](/app/Todo/TodoListWithActions)** - Full CRUD operations with action buttons

## Features Demonstrated

✅ **Entity Management**: CRUD operations with `DataChangeRequest`  
✅ **Reactive Streams**: Real-time updates across all views  
✅ **Form Controls**: Interactive forms with validation  
✅ **Status Workflow**: Todo lifecycle management  
✅ **Rich UI**: Markdown formatting with emojis and styling  
✅ **Data Persistence**: Automatic state management  

## Try It Out!

1. **View existing todos** in the [Todo List](/app/Todo/TodoList)
2. **Add a new todo** using the [Add Todo](/app/Todo/AddTodo) form
3. **Manage todos** with [Todo Management](/app/Todo/TodoListWithActions) actions
4. **Watch real-time updates** as changes propagate across views

The application includes sample data to get you started exploring the functionality.
                "))
        );
    }

    /// <summary>
    /// Creates action buttons for a todo item based on its status
    /// </summary>
    /// <param name="todo">The todo item</param>
    /// <param name="host">The layout area host for submitting changes</param>
    /// <returns>A stack control with action buttons</returns>
    private static UiControl CreateActionButtons(TodoItem todo, LayoutAreaHost host)
    {
        var buttonStack = Controls.Stack
            .WithHorizontalGap(8)
            .WithOrientation(Orientation.Horizontal);

        if (todo.Status == TodoStatus.Pending)
        {
            buttonStack = buttonStack
                .WithView(Controls.Button("Start")
                    .WithClickAction(_ =>
                    {
                        var updatedTodo = todo with
                        {
                            Status = TodoStatus.InProgress,
                            UpdatedAt = DateTime.UtcNow
                        };
                        SubmitTodoUpdate(host, updatedTodo);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("Complete")
                    .WithClickAction(_ =>
                    {
                        var updatedTodo = todo with
                        {
                            Status = TodoStatus.Completed,
                            UpdatedAt = DateTime.UtcNow
                        };
                        SubmitTodoUpdate(host, updatedTodo);
                        return Task.CompletedTask;
                    }));
        }
        else if (todo.Status == TodoStatus.InProgress)
        {
            buttonStack = buttonStack
                .WithView(Controls.Button("Complete")
                    .WithClickAction(_ =>
                    {
                        var updatedTodo = todo with
                        {
                            Status = TodoStatus.Completed,
                            UpdatedAt = DateTime.UtcNow
                        };
                        SubmitTodoUpdate(host, updatedTodo);
                        return Task.CompletedTask;
                    }))
                .WithView(Controls.Button("Back to Pending")
                    .WithClickAction(_ =>
                    {
                        var updatedTodo = todo with
                        {
                            Status = TodoStatus.Pending,
                            UpdatedAt = DateTime.UtcNow
                        };
                        SubmitTodoUpdate(host, updatedTodo);
                        return Task.CompletedTask;
                    }));
        }
        else if (todo.Status == TodoStatus.Completed)
        {
            buttonStack = buttonStack
                .WithView(Controls.Button("Reopen")
                    .WithClickAction(_ =>
                    {
                        var updatedTodo = todo with
                        {
                            Status = TodoStatus.Pending,
                            UpdatedAt = DateTime.UtcNow
                        };
                        SubmitTodoUpdate(host, updatedTodo);
                        return Task.CompletedTask;
                    }));
        }

        // Always add delete button
        buttonStack = buttonStack
            .WithView(Controls.Button("Delete")
                .WithClickAction(_ =>
                {
                    SubmitTodoDelete(host, todo);
                    return Task.CompletedTask;
                }));

        return buttonStack;
    }
}

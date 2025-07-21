using MeshWeaver.Todo.Domain;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Todo.SampleData;

/// <summary>
/// Sample data for testing the Todo application
/// </summary>
public static class TodoSampleData
{
    private static readonly DateTime BaseDate = DateTime.UtcNow;

    /// <summary>
    /// Gets sample todo categories for testing and demonstration
    /// </summary>
    /// <returns>A collection of sample TodoCategory objects</returns>
    public static IEnumerable<TodoCategory> GetSampleCategories() =>
    [
        new TodoCategory
        {
            Name = "Work",
            DisplayName = "Work",
            Description = "Professional tasks, meetings, projects, and work-related activities"
        },
        new TodoCategory
        {
            Name = "Personal",
            DisplayName = "Personal",
            Description = "Personal errands, household tasks, and family-related activities"
        },
        new TodoCategory
        {
            Name = "Health",
            DisplayName = "Health",
            Description = "Health and fitness related activities, medical appointments, and wellness tasks"
        },
        new TodoCategory
        {
            Name = "Learning",
            DisplayName = "Learning",
            Description = "Educational activities, courses, reading, and skill development"
        },
        new TodoCategory
        {
            Name = "Career",
            DisplayName = "Career",
            Description = "Career development, networking, job searching, and professional growth"
        },
        new TodoCategory
        {
            Name = "Finance",
            DisplayName = "Finance",
            Description = "Financial planning, budgeting, investments, and money-related tasks"
        },
        new TodoCategory
        {
            Name = "Travel",
            DisplayName = "Travel",
            Description = "Travel planning, bookings, itineraries, and vacation-related tasks"
        },
        new TodoCategory
        {
            Name = "General",
            DisplayName = "General",
            Description = "Miscellaneous tasks that don't fit into specific categories"
        }
    ];

    /// <summary>
    /// Gets sample todo items for testing and demonstration
    /// </summary>
    /// <returns>A collection of sample TodoItem objects</returns>
    public static IEnumerable<TodoItem> GetSampleTodos() =>
    [
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Complete project documentation",
            Description = "Write comprehensive documentation for the MeshWeaver Todo module",
            Category = "Work",
            DueDate = BaseDate.AddDays(3),
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-5),
            UpdatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Review pull request #123",
            Description = "Review the new authentication feature implementation",
            Category = "Work",
            DueDate = BaseDate.AddDays(1),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Buy groceries",
            Description = "Milk, bread, eggs, and fresh vegetables",
            Category = "Personal",
            DueDate = BaseDate.AddDays(1),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Plan vacation",
            Description = "Research destinations and book flights for summer vacation",
            Category = "Travel",
            DueDate = BaseDate.AddDays(14),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-3)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Fix production bug",
            Description = "Critical bug affecting user login functionality",
            Category = "Work",
            DueDate = BaseDate.AddDays(-1), // Overdue
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-4),
            UpdatedAt = BaseDate.AddHours(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Learn TypeScript",
            Description = "Complete online TypeScript course and practice examples",
            Category = "Learning",
            DueDate = BaseDate.AddDays(21),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-7)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Team meeting preparation",
            Description = "Prepare slides and agenda for tomorrow's team meeting",
            Category = "Work",
            DueDate = BaseDate, // Due today
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Update resume",
            Description = "Add recent projects and skills to professional resume",
            Category = "Career",
            Status = TodoStatus.Completed,
            CreatedAt = BaseDate.AddDays(-10),
            UpdatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Gym workout",
            Description = "Cardio and strength training session",
            Category = "Health",
            Status = TodoStatus.Cancelled,
            CreatedAt = BaseDate.AddDays(-5),
            UpdatedAt = BaseDate.AddDays(-4)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Read technical book",
            Description = "Continue reading 'Clean Architecture' by Robert Martin",
            Category = "Learning",
            DueDate = BaseDate.AddDays(7),
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-14),
            UpdatedAt = BaseDate.AddDays(-3)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Review monthly budget",
            Description = "Analyze spending patterns and adjust budget for next month",
            Category = "Finance",
            DueDate = BaseDate.AddDays(5),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Book hotel for conference",
            Description = "Find and book accommodation for the upcoming tech conference",
            Category = "Travel",
            DueDate = BaseDate.AddDays(10),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        }
    ];
}

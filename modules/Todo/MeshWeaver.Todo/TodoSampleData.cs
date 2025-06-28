namespace MeshWeaver.Todo;

/// <summary>
/// Sample data for testing the Todo application
/// </summary>
public static class TodoSampleData
{
    private static readonly DateTime BaseDate = DateTime.UtcNow;
    /// <summary>
    /// Gets sample todo items for testing and demonstration
    /// </summary>
    /// <returns>A collection of sample TodoItem objects</returns>
    public static IEnumerable<TodoItem> GetSampleTodos() =>
    [
        new TodoItem
        {
            Id = Guid.NewGuid(),
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
            Id = Guid.NewGuid(),
            Title = "Review pull request #123",
            Description = "Review the new authentication feature implementation",
            Category = "Work",
            DueDate = BaseDate.AddDays(1),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Buy groceries",
            Description = "Milk, bread, eggs, and fresh vegetables",
            Category = "Personal",
            DueDate = BaseDate.AddDays(1),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Plan vacation",
            Description = "Research destinations and book flights for summer vacation",
            Category = "Personal",
            DueDate = BaseDate.AddDays(14),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-3)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
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
            Id = Guid.NewGuid(),
            Title = "Learn TypeScript",
            Description = "Complete online TypeScript course and practice examples",
            Category = "Learning",
            DueDate = BaseDate.AddDays(21),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-7)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Team meeting preparation",
            Description = "Prepare slides and agenda for tomorrow's team meeting",
            Category = "Work",
            DueDate = BaseDate, // Due today
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Update resume",
            Description = "Add recent projects and skills to professional resume",
            Category = "Career",
            Status = TodoStatus.Completed,
            CreatedAt = BaseDate.AddDays(-10),
            UpdatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Gym workout",
            Description = "Cardio and strength training session",
            Category = "Health",
            Status = TodoStatus.Cancelled,
            CreatedAt = BaseDate.AddDays(-5),
            UpdatedAt = BaseDate.AddDays(-4)
        },
        new TodoItem
        {
            Id = Guid.NewGuid(),
            Title = "Read technical book",
            Description = "Continue reading 'Clean Architecture' by Robert Martin",
            Category = "Learning",
            DueDate = BaseDate.AddDays(7),
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-14),
            UpdatedAt = BaseDate.AddDays(-3)
        }
    ];
}

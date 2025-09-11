using MeshWeaver.Todo.Domain;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Todo.SampleData;

/// <summary>
/// Sample data for testing the Todo application
/// </summary>
public static class TodoSampleData
{
    private static readonly DateTime BaseDate = DateTime.Now.Date;

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
    public static IEnumerable<TodoItem> GetSampleTodos()
    {
        var random = new Random(42); // Fixed seed for consistent results
        var persons = ResponsiblePersons.AvailablePersons;
        
        return [
        // OVERDUE TASKS - One assigned to current user, others to team members (no unassigned overdue)
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Security audit review",
            Description = "Review and address findings from the quarterly security audit - URGENT!",
            Category = "Work",
            ResponsiblePerson = ResponsiblePersons.GetCurrentUser(), // Assigned to current user (overdue)
            DueDate = BaseDate.AddDays(-2), // Overdue
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-7),
            UpdatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Fix production bug",
            Description = "Critical bug affecting user login functionality",
            Category = "Work",
            ResponsiblePerson = "Jordan Smith", // Assigned to team member
            DueDate = BaseDate.AddDays(-1), // Overdue
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-4),
            UpdatedAt = BaseDate.AddHours(-2)
        },

        // DUE TODAY TASKS - Mix of current user and team members
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Team meeting preparation",
            Description = "Prepare slides and agenda for today's team meeting",
            Category = "Work",
            ResponsiblePerson = ResponsiblePersons.GetCurrentUser(), // Current user
            DueDate = BaseDate, // Due today
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Mobile app bug fixes",
            Description = "Fix critical bugs reported in the latest mobile app release",
            Category = "Work",
            ResponsiblePerson = "Riley Chen", // Team member
            DueDate = BaseDate, // Due today
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-4)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Code review: authentication module",
            Description = "Review Jordan's implementation of two-factor authentication",
            Category = "Work",
            ResponsiblePerson = ResponsiblePersons.GetCurrentUser(), // Current user
            DueDate = BaseDate.AddDays(5), // Future task
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-1),
            UpdatedAt = BaseDate.AddHours(-3)
        },

        // FUTURE TASKS - Mostly in progress or completed, fewer pending
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Complete project documentation",
            Description = "Write comprehensive documentation for the MeshWeaver Todo module",
            Category = "Work",
            ResponsiblePerson = "Avery Taylor",
            DueDate = BaseDate.AddDays(3),
            Status = TodoStatus.InProgress,
            CreatedAt = BaseDate.AddDays(-5),
            UpdatedAt = BaseDate.AddHours(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Client presentation",
            Description = "Prepare quarterly business review presentation for BigCorp client",
            Category = "Work",
            ResponsiblePerson = ResponsiblePersons.GetCurrentUser(),
            DueDate = BaseDate.AddDays(1), // Due tomorrow
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-3)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Database migration script",
            Description = "Write and test migration scripts for the new user preferences table",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(2),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },

        // MORE UNASSIGNED TASKS - All future due dates for better planning demo
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "API documentation update",
            Description = "Update REST API documentation with new endpoints",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(8),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Docker containerization",
            Description = "Containerize the microservices for better deployment",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(21),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-3)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Performance testing suite",
            Description = "Create automated performance tests for the web application",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(14),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "User feedback analysis",
            Description = "Analyze Q3 user feedback and create improvement recommendations",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(10),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Backup system verification",
            Description = "Test and verify that all backup systems are working correctly",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(7),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-1)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Security vulnerability scan",
            Description = "Run comprehensive security scan and address any findings",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(5),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Mobile app beta testing",
            Description = "Coordinate beta testing program for the new mobile app features",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(12),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Database optimization",
            Description = "Optimize database queries and indexes for better performance",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(18),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-4)
        },

        // COMPLETED AND ONGOING TASKS
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Design user interface mockups",
            Description = "Create wireframes and mockups for the new dashboard feature",
            Category = "Work",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(5),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-3)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Quarterly expense report",
            Description = "Compile and submit Q3 expense reports",
            Category = "Finance",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(3),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-5)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Read technical book",
            Description = "Continue reading 'Clean Architecture' by Robert Martin",
            Category = "Learning",
            ResponsiblePerson = "Unassigned",
            DueDate = BaseDate.AddDays(7),
            Status = TodoStatus.Pending,
            CreatedAt = BaseDate.AddDays(-14)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Update resume",
            Description = "Add recent projects and skills to professional resume",
            Category = "Career",
            ResponsiblePerson = "Sage Williams",
            Status = TodoStatus.Completed,
            CreatedAt = BaseDate.AddDays(-10),
            UpdatedAt = BaseDate.AddDays(-2)
        },
        new TodoItem
        {
            Id = Guid.NewGuid().AsString(),
            Title = "Team lunch coordination",
            Description = "Organize monthly team lunch and book restaurant",
            Category = "Personal",
            ResponsiblePerson = "Cameron Davis",
            Status = TodoStatus.Completed,
            CreatedAt = BaseDate.AddDays(-12),
            UpdatedAt = BaseDate.AddDays(-8)
        }
    ];
    }
}

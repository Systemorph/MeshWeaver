// <meshweaver>
// Id: Status
// DisplayName: Project Status Data Model
// </meshweaver>

/// <summary>
/// Represents a task status with display metadata.
/// </summary>
public record Status
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Emoji { get; init; } = string.Empty;

    public int Order { get; init; }

    /// <summary>
    /// Whether groups with this status should be expanded by default in catalog views.
    /// </summary>
    public bool IsExpandedByDefault { get; init; } = true;

    public static readonly Status Pending = new()
    {
        Id = "Pending", Name = "Pending", Emoji = "\u23f3",
        Description = "Task is waiting to be started", Order = 0, IsExpandedByDefault = true
    };

    public static readonly Status InProgress = new()
    {
        Id = "InProgress", Name = "In Progress", Emoji = "\ud83d\udd04",
        Description = "Task is actively being worked on", Order = 1, IsExpandedByDefault = true
    };

    public static readonly Status InReview = new()
    {
        Id = "InReview", Name = "In Review", Emoji = "\ud83d\udc41\ufe0f",
        Description = "Task is being reviewed", Order = 2, IsExpandedByDefault = true
    };

    public static readonly Status Blocked = new()
    {
        Id = "Blocked", Name = "Blocked", Emoji = "\ud83d\udeab",
        Description = "Task is blocked by dependencies", Order = 3, IsExpandedByDefault = true
    };

    public static readonly Status Completed = new()
    {
        Id = "Completed", Name = "Completed", Emoji = "\u2705",
        Description = "Task has been completed", Order = 4, IsExpandedByDefault = false
    };

    public static readonly Status[] All = [Pending, InProgress, InReview, Blocked, Completed];

    public static Status GetById(string? id) =>
        All.FirstOrDefault(s => s.Id == id) ?? Pending;
}

// <meshweaver>
// Id: Status
// DisplayName: Project Status Data Model
// </meshweaver>

/// <summary>
/// Represents a project status with display metadata.
/// </summary>
public record Status
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public int Order { get; init; }

    public static readonly Status Planning = new()
    {
        Id = "Planning", Name = "Planning",
        Description = "Project is in planning phase", Order = 1
    };

    public static readonly Status Active = new()
    {
        Id = "Active", Name = "Active",
        Description = "Project is actively being worked on", Order = 2
    };

    public static readonly Status OnHold = new()
    {
        Id = "OnHold", Name = "On Hold",
        Description = "Project is temporarily paused", Order = 3
    };

    public static readonly Status Completed = new()
    {
        Id = "Completed", Name = "Completed",
        Description = "Project has been completed", Order = 4
    };

    public static readonly Status Cancelled = new()
    {
        Id = "Cancelled", Name = "Cancelled",
        Description = "Project has been cancelled", Order = 5
    };

    public static readonly Status[] All = [Planning, Active, OnHold, Completed, Cancelled];
}

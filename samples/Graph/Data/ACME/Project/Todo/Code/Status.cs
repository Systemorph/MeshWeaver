// <meshweaver>
// Id: Status
// DisplayName: Project Status Data Model
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Represents a project status with display metadata.
/// </summary>
public record Status : INamed
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Emoji { get; init; } = string.Empty;

    public int Order { get; init; }

    /// <summary>
    /// Display name with emoji prefix for UI display.
    /// </summary>
    [Browsable(false)]
    public string DisplayName => string.IsNullOrEmpty(Emoji) ? Name : $"{Emoji} {Name}";

    public static readonly Status Planning = new()
    {
        Id = "Planning", Name = "Planning", Emoji = "📋",
        Description = "Project is in planning phase", Order = 1
    };

    public static readonly Status Active = new()
    {
        Id = "Active", Name = "Active", Emoji = "🚀",
        Description = "Project is actively being worked on", Order = 2
    };

    public static readonly Status OnHold = new()
    {
        Id = "OnHold", Name = "On Hold", Emoji = "⏸️",
        Description = "Project is temporarily paused", Order = 3
    };

    public static readonly Status Completed = new()
    {
        Id = "Completed", Name = "Completed", Emoji = "✅",
        Description = "Project has been completed", Order = 4
    };

    public static readonly Status Cancelled = new()
    {
        Id = "Cancelled", Name = "Cancelled", Emoji = "❌",
        Description = "Project has been cancelled", Order = 5
    };

    public static readonly Status[] All = [Planning, Active, OnHold, Completed, Cancelled];

    public static Status? GetById(string? id) => All.FirstOrDefault(s => s.Id == id);
}

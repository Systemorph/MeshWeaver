// <meshweaver>
// Id: Priority
// DisplayName: Project Priority Data Model
// </meshweaver>

/// <summary>
/// Represents a task priority with display metadata.
/// </summary>
public record Priority
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string Emoji { get; init; } = string.Empty;

    public int Order { get; init; }

    /// <summary>
    /// Whether groups with this priority should be expanded by default in catalog views.
    /// </summary>
    public bool IsExpandedByDefault { get; init; } = true;

    public static readonly Priority Critical = new()
    {
        Id = "Critical", Name = "Critical Priority", Emoji = "\ud83d\udea8", Order = 0, IsExpandedByDefault = true
    };

    public static readonly Priority High = new()
    {
        Id = "High", Name = "High Priority", Emoji = "\ud83d\udd25", Order = 1, IsExpandedByDefault = true
    };

    public static readonly Priority Medium = new()
    {
        Id = "Medium", Name = "Medium Priority", Emoji = "\ud83d\udfe1", Order = 2, IsExpandedByDefault = false
    };

    public static readonly Priority Low = new()
    {
        Id = "Low", Name = "Low Priority", Emoji = "\ud83d\udfe2", Order = 3, IsExpandedByDefault = false
    };

    public static readonly Priority Unset = new()
    {
        Id = "Unset", Name = "Unset Priority", Emoji = "\u2753", Order = 4, IsExpandedByDefault = false
    };

    public static readonly Priority[] All = [Critical, High, Medium, Low, Unset];

    public static Priority GetById(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? Unset;
}

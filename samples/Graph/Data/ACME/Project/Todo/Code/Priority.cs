// <meshweaver>
// Id: Priority
// DisplayName: Project Priority Data Model
// </meshweaver>

using MeshWeaver.Domain;

/// <summary>
/// Represents a task priority with display metadata and styling.
/// </summary>
public record Priority : INamed
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string Emoji { get; init; } = string.Empty;

    /// <summary>
    /// Background color for priority badges (hex format).
    /// </summary>
    public string BackgroundColor { get; init; } = "#6c757d";

    /// <summary>
    /// Text color for priority badges (hex format).
    /// </summary>
    public string TextColor { get; init; } = "#fff";

    public int Order { get; init; }

    /// <summary>
    /// Whether groups with this priority should be expanded by default in catalog views.
    /// </summary>
    public bool IsExpandedByDefault { get; init; } = true;

    /// <summary>
    /// Display name with emoji prefix for UI display.
    /// </summary>
    [Browsable(false)]
    public string DisplayName => string.IsNullOrEmpty(Emoji) ? Name : $"{Emoji} {Name}";

    public static readonly Priority Critical = new()
    {
        Id = "Critical", Name = "Critical", Emoji = "🚨", Order = 0,
        BackgroundColor = "#dc3545", TextColor = "#fff", IsExpandedByDefault = true
    };

    public static readonly Priority High = new()
    {
        Id = "High", Name = "High", Emoji = "🔥", Order = 1,
        BackgroundColor = "#fd7e14", TextColor = "#fff", IsExpandedByDefault = true
    };

    public static readonly Priority Medium = new()
    {
        Id = "Medium", Name = "Medium", Emoji = "🟡", Order = 2,
        BackgroundColor = "#ffc107", TextColor = "#000", IsExpandedByDefault = false
    };

    public static readonly Priority Low = new()
    {
        Id = "Low", Name = "Low", Emoji = "🟢", Order = 3,
        BackgroundColor = "#198754", TextColor = "#fff", IsExpandedByDefault = false
    };

    public static readonly Priority Unset = new()
    {
        Id = "Unset", Name = "Unset", Emoji = "❓", Order = 4,
        BackgroundColor = "#6c757d", TextColor = "#fff", IsExpandedByDefault = false
    };

    public static readonly Priority[] All = [Critical, High, Medium, Low, Unset];

    public static Priority GetById(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? Unset;
}

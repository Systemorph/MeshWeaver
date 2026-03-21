// <meshweaver>
// Id: Category
// DisplayName: Project Category Data Model
// </meshweaver>

/// <summary>
/// Represents a task category with display metadata and emoji.
/// </summary>
public record Category
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Emoji { get; init; } = "\ud83d\udcc1";

    public int Order { get; init; }

    public static readonly Category Design = new()
    {
        Id = "Design", Name = "Design",
        Description = "Design and UX tasks", Emoji = "\ud83c\udfa8", Order = 1
    };

    public static readonly Category Engineering = new()
    {
        Id = "Engineering", Name = "Engineering",
        Description = "Development and technical tasks", Emoji = "\u2699\ufe0f", Order = 2
    };

    public static readonly Category Marketing = new()
    {
        Id = "Marketing", Name = "Marketing",
        Description = "Marketing and promotion tasks", Emoji = "\ud83d\udce3", Order = 3
    };

    public static readonly Category Sales = new()
    {
        Id = "Sales", Name = "Sales",
        Description = "Sales and business development tasks", Emoji = "\ud83d\udcb0", Order = 4
    };

    public static readonly Category Research = new()
    {
        Id = "Research", Name = "Research",
        Description = "Research and analysis tasks", Emoji = "\ud83d\udd2c", Order = 5
    };

    public static readonly Category Legal = new()
    {
        Id = "Legal", Name = "Legal",
        Description = "Legal and compliance tasks", Emoji = "\u2696\ufe0f", Order = 6
    };

    public static readonly Category Support = new()
    {
        Id = "Support", Name = "Support",
        Description = "Customer support tasks", Emoji = "\ud83d\udee0\ufe0f", Order = 7
    };

    public static readonly Category PR = new()
    {
        Id = "PR", Name = "PR",
        Description = "Public relations tasks", Emoji = "\ud83d\udcf0", Order = 8
    };

    public static readonly Category Strategy = new()
    {
        Id = "Strategy", Name = "Strategy",
        Description = "Strategic planning tasks", Emoji = "\ud83c\udfaf", Order = 9
    };

    public static readonly Category Partnerships = new()
    {
        Id = "Partnerships", Name = "Partnerships",
        Description = "Partnership and collaboration tasks", Emoji = "\ud83e\udd1d", Order = 10
    };

    public static readonly Category Uncategorized = new()
    {
        Id = "Uncategorized", Name = "Uncategorized",
        Description = "Tasks without a category", Emoji = "\ud83d\udccb", Order = 99
    };

    public static readonly Category[] All = [Design, Engineering, Marketing, Sales, Research, Legal, Support, PR, Strategy, Partnerships, Uncategorized];

    public static Category? GetById(string? id) => All.FirstOrDefault(c => c.Id == id) ?? Uncategorized;
}

// <meshweaver>
// Id: Platform
// DisplayName: Social Media Platform
// </meshweaver>

public record Platform
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Name { get; init; } = string.Empty;

    public string Emoji { get; init; } = string.Empty;

    public string Color { get; init; } = "#0a66c2";

    public int Order { get; init; }

    public static readonly Platform LinkedIn = new()
    {
        Id = "LinkedIn", Name = "LinkedIn", Emoji = "\ud83d\udcbc", Color = "#0a66c2", Order = 0
    };

    public static readonly Platform Twitter = new()
    {
        Id = "Twitter", Name = "X / Twitter", Emoji = "\ud83d\udc26", Color = "#000000", Order = 1
    };

    public static readonly Platform Instagram = new()
    {
        Id = "Instagram", Name = "Instagram", Emoji = "\ud83d\udcf7", Color = "#e1306c", Order = 2
    };

    public static readonly Platform[] All = [LinkedIn, Twitter, Instagram];

    public static Platform GetById(string? id) =>
        All.FirstOrDefault(p => p.Id == id) ?? LinkedIn;
}

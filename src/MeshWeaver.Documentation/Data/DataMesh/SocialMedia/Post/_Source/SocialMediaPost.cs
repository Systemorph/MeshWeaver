// <meshweaver>
// Id: SocialMediaPost
// DisplayName: Social Media Post
// </meshweaver>

using MeshWeaver.Domain;

public record SocialMediaPost
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    [UiControl(Style = "width: 100%;")]
    public string Title { get; init; } = string.Empty;

    [Markdown(EditorHeight = "200px")]
    public string? Body { get; init; }

    [Required]
    [DisplayName("Profile path")]
    public string ProfilePath { get; init; } = string.Empty;

    [Dimension<Platform>]
    [UiControl(Style = "width: 200px;")]
    public string Platform { get; init; } = "LinkedIn";

    [DisplayName("Scheduled at")]
    public DateTimeOffset? ScheduledAt { get; init; }

    [DisplayName("Published at")]
    public DateTimeOffset? PublishedAt { get; init; }

    public int Impressions { get; init; }

    public int Likes { get; init; }
}

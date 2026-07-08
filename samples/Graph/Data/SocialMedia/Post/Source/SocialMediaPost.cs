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

    public int Comments { get; init; }

    [DisplayName("Media URL")]
    public string? MediaUrl { get; init; }

    // Publishing lifecycle: Draft → Published (or Failed). Written back by the
    // /linkedin/publish endpoint; drives the Post-node "Publish to LinkedIn" menu action.
    public string Status { get; init; } = "Draft";

    [DisplayName("Published URN")]
    public string? PublishedUrn { get; init; }
}

// <meshweaver>
// Id: SocialMediaProfile
// DisplayName: Social Media Profile
// </meshweaver>

using MeshWeaver.Domain;

public record SocialMediaProfile
{
    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    public string Name { get; init; } = string.Empty;

    [Required]
    [Dimension<Platform>]
    [UiControl(Style = "width: 200px;")]
    public string Platform { get; init; } = "LinkedIn";

    [Required]
    [DisplayName("Owner email")]
    public string Owner { get; init; } = string.Empty;

    [DisplayName("Profile URL")]
    public string? ProfileUrl { get; init; }

    [Markdown(EditorHeight = "120px")]
    public string? Bio { get; init; }
}

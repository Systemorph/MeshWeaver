// <meshweaver>
// Id: Insured
// DisplayName: Insured Data Model
// </meshweaver>

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;

/// <summary>
/// Represents an insurance client (the insured party).
/// </summary>
public record Insured
{
    [Key]
    public string Id { get; init; } = string.Empty;

    [Required]
    [MeshNodeProperty(nameof(MeshNode.Name))]
    [Description("Legal name of the insured company or individual")]
    public string Name { get; init; } = string.Empty;

    [Description("Brief description of the insured's business activities")]
    public string? Description { get; init; }

    /// <summary>
    /// Physical address of the insured.
    /// </summary>
    [Description("Physical headquarters or primary business address")]
    public string? Address { get; init; }

    [Description("Company website URL")]
    [Url(ErrorMessage = "Please enter a valid website URL")]
    public string? Website { get; init; }

    [Description("Primary contact email address")]
    [EmailAddress(ErrorMessage = "Please enter a valid email address")]
    public string? Email { get; init; }

    [Description("Industry sector or business classification")]
    public string? Industry { get; init; }

    [Description("Primary geographic location of operations")]
    public string? Location { get; init; }
}

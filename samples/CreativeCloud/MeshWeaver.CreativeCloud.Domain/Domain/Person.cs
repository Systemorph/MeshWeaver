using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Represents a person (content creator) in the content portal.
/// </summary>
[Display(GroupName = "People")]
public record Person : INamed
{
    /// <summary>
    /// Unique identifier for the person.
    /// </summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>
    /// First name of the person.
    /// </summary>
    public required string FirstName { get; init; }

    /// <summary>
    /// Last name of the person.
    /// </summary>
    public required string LastName { get; init; }

    /// <summary>
    /// Company or organization the person is associated with.
    /// </summary>
    public string? Company { get; init; }

    /// <summary>
    /// Email address of the person.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Reference to the person's content archetype.
    /// </summary>
    [Dimension<ContentArchetype>]
    public string? ContentArchetypeId { get; init; }

    string INamed.DisplayName => $"{FirstName} {LastName}";
}

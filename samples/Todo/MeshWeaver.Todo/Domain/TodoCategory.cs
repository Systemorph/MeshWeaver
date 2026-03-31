using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;

namespace MeshWeaver.Todo.Domain;

/// <summary>
/// Represents a category for organizing todo items
/// </summary>
/// <remarks>
/// Categories provide a way to group related todo items together for better organization and filtering.
/// Examples include "Work", "Personal", "Health", "Learning", etc.
/// </remarks>
public record TodoCategory : INamed
{
    /// <summary>
    /// Gets or sets the name of the category
    /// </summary>
    /// <value>A unique identifier for the category (e.g., "Work", "Personal", "Health")</value>
    [Key]public required string Name { get; init; }

    /// <summary>
    /// Display name for the category
    /// </summary>
    public required string DisplayName { get; init; }
    /// <summary>
    /// Gets or sets the description of the category
    /// </summary>
    /// <value>A detailed description explaining the purpose and scope of this category</value>
    public required string Description { get; init; }

}

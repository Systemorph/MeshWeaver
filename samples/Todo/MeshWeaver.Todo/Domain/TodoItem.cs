using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.ShortGuid;


namespace MeshWeaver.Todo.Domain;

/// <summary>
/// Represents a to-do item in the application.
/// </summary>
public record TodoItem
{
    /// <summary>
    /// Gets the unique identifier for the to-do item.
    /// </summary>
    [Key]
    [Browsable(false)]
    public string Id { get; init; } = Guid.NewGuid().AsString();

    /// <summary>
    /// Gets the title of the to-do item.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the description of the to-do item.
    /// </summary>
    [UiControl<TextAreaControl>]
    public string? Description { get; init; }

    /// <summary>
    /// Gets the category of the to-do item. This must be one of the official categories defined in the system.
    /// </summary>
    [Dimension<TodoCategory>]
    public string Category { get; init; } = "General";

    /// <summary>
    /// Gets the responsible person assigned to this to-do item.
    /// </summary>
    public string ResponsiblePerson { get; init; } = "Unassigned";

    /// <summary>
    /// Gets the due date of the to-do item, if any.
    /// </summary>
    public DateTime? DueDate { get; init; }

    /// <summary>
    /// Gets the status of the to-do item.
    /// </summary>
    [Editable(false)]
    public TodoStatus Status { get; init; } = TodoStatus.Pending;

    /// <summary>
    /// Gets the creation date and time of the to-do item.
    /// </summary>
    [Editable(false)]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the last updated date and time of the to-do item, if any.
    /// </summary>
    [Browsable(false)]
    public DateTime? UpdatedAt { get; init; }
}
